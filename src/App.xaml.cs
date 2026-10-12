using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RAMMapTray
{
    public partial class App : System.Windows.Application
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern unsafe int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern unsafe bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private static Mutex? _singleInstanceMutex;
        private static bool _hasMutexOwnership = false;
        private TrayIconManager? _trayManager;
        private FlyoutWindow? _flyoutWindow;
        private DispatcherTimer? _timer;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 && e.Args[0] == "--purge-standby")
            {
                NativeMemory.EmptyStandbyList();
                Shutdown(0);
                return;
            }

            try
            {
                _singleInstanceMutex = new Mutex(true, "RAMMapTrayCompanionMutex_UniqueId", out _hasMutexOwnership);
            }
            catch
            {
                _hasMutexOwnership = false;
            }

            if (!_hasMutexOwnership)
            {
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            try
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
            }
            catch { }

            _flyoutWindow = new FlyoutWindow(LaunchRAMMapExecutable);

            // Pre-initialize native window handle so the first click shows instantly without cold-start delay
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(_flyoutWindow);
                helper.EnsureHandle();
                _flyoutWindow.PositionNearTray();
            }
            catch { }

            _trayManager = new TrayIconManager(
                onToggleFlyout: ToggleFlyoutWindow,
                onOpenRAMMap: LaunchRAMMapExecutable,
                onPurgeAll: PurgeAllMemory,
                onPurgeWorkingSets: PurgeWorkingSetsMemory,
                onPurgeStandby: PurgeStandbyMemory
            );

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            RefreshMemoryNow();
        }

        private bool _isRefreshing = false;
        private DateTime _lastToggleTime = DateTime.MinValue;

        private void Timer_Tick(object? sender, EventArgs e)
        {
            RefreshMemoryNow();
        }

        public void RefreshMemoryNow()
        {
            _ = RefreshMemoryAsync();
        }

        public async Task RefreshMemoryAsync()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                MemoryStatusInfo memInfo = await Task.Run(() => NativeMemory.GetMemoryStatus());
                int pct = (int)Math.Round(memInfo.MemoryLoadPercentage);
                double usedGB = memInfo.UsedMemoryBytes / (1024.0 * 1024 * 1024);
                double totalGB = memInfo.TotalMemoryBytes / (1024.0 * 1024 * 1024);

                string tooltip = $"RAM: {pct}% ({usedGB:F1} GB / {totalGB:F1} GB)";
                _trayManager?.UpdateIcon(pct, tooltip);

                if (_flyoutWindow != null && _flyoutWindow.IsVisible)
                {
                    _flyoutWindow.UpdateMetrics(memInfo);
                }
            }
            catch
            {
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void ToggleFlyoutWindow()
        {
            if (_flyoutWindow == null) return;

            if ((DateTime.UtcNow - _lastToggleTime).TotalMilliseconds < 250 ||
                (DateTime.UtcNow - _flyoutWindow.LastHideTime).TotalMilliseconds < 250)
            {
                return;
            }
            _lastToggleTime = DateTime.UtcNow;

            if (_flyoutWindow.IsVisible)
            {
                _flyoutWindow.Hide();
            }
            else
            {
                _flyoutWindow.PositionNearTray();
                _flyoutWindow.Show();
                _flyoutWindow.Activate();
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(_flyoutWindow);
                    if (helper.Handle != IntPtr.Zero)
                    {
                        SetForegroundWindow(helper.Handle);
                    }
                }
                catch { }
                _flyoutWindow.PositionNearTray();
                _flyoutWindow.RecordShowTime();
                RefreshMemoryNow();
            }
        }

        private void PurgeAllMemory()
        {
            Task.Run(() =>
            {
                NativeMemory.PurgeAll();
                Dispatcher.Invoke(RefreshMemoryNow);
            });
        }

        private void PurgeWorkingSetsMemory()
        {
            Task.Run(() =>
            {
                NativeMemory.EmptyWorkingSets();
                Dispatcher.Invoke(RefreshMemoryNow);
            });
        }

        private void PurgeStandbyMemory()
        {
            Task.Run(() =>
            {
                NativeMemory.EmptyStandbyList();
                Dispatcher.Invoke(RefreshMemoryNow);
            });
        }

        private unsafe bool TryActivateRunningRAMMap()
        {
            IntPtr targetHwnd = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd) && !IsIconic(hWnd))
                    return true;

                char* titleBuf = stackalloc char[256];
                int len = GetWindowTextW(hWnd, titleBuf, 256);
                if (len > 0)
                {
                    ReadOnlySpan<char> title = new ReadOnlySpan<char>(titleBuf, len);
                    if (title.StartsWith("RAMMap", StringComparison.OrdinalIgnoreCase) &&
                        !title.Contains("Companion", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHwnd = hWnd;
                        return false;
                    }
                }

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                {
                    IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            char* nameBuf = stackalloc char[260];
                            uint size = 260;
                            if (QueryFullProcessImageNameW(hProc, 0, nameBuf, ref size) && size > 0)
                            {
                                ReadOnlySpan<char> fullPath = new ReadOnlySpan<char>(nameBuf, (int)size);
                                int lastSlash = fullPath.LastIndexOf('\\');
                                ReadOnlySpan<char> exeName = lastSlash >= 0 ? fullPath.Slice(lastSlash + 1) : fullPath;
                                if (exeName.StartsWith("RAMMap", StringComparison.OrdinalIgnoreCase) &&
                                    !exeName.Equals("RAMMapTray.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    targetHwnd = hWnd;
                                    return false;
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (targetHwnd != IntPtr.Zero)
            {
                if (IsIconic(targetHwnd))
                    ShowWindowAsync(targetHwnd, SW_RESTORE);
                else
                    ShowWindowAsync(targetHwnd, SW_SHOW);
                SetForegroundWindow(targetHwnd);
                return true;
            }

            return false;
        }

        private void LaunchRAMMapExecutable()
        {
            try
            {
                if (TryActivateRunningRAMMap())
                {
                    return;
                }

                string[] candidateDirs = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Directory.GetCurrentDirectory(),
                    @"c:\Users\david\Documents\RAMMap",
                    Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) ?? ""
                };

                string[] targetFiles = { "RAMMap64.exe", "RAMMap.exe", "RAMMap64a.exe" };
                string foundPath = string.Empty;

                foreach (var dir in candidateDirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var name in targetFiles)
                    {
                        string test = Path.Combine(dir, name);
                        if (File.Exists(test))
                        {
                            foundPath = test;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(foundPath)) break;
                }

                if (!string.IsNullOrEmpty(foundPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = foundPath,
                        WorkingDirectory = Path.GetDirectoryName(foundPath),
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                else
                {
                    System.Windows.MessageBox.Show("No se encontró el ejecutable RAMMap64.exe en la carpeta del proyecto.", "RAMMap Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error al ejecutar RAMMap: {ex.Message}", "RAMMap Companion", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _timer?.Stop();
            _trayManager?.Dispose();
            if (_hasMutexOwnership)
            {
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            }
            try { _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
