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

        private void Timer_Tick(object? sender, EventArgs e)
        {
            RefreshMemoryNow();
        }

        public void RefreshMemoryNow()
        {
            try
            {
                MemoryStatusInfo memInfo = NativeMemory.GetMemoryStatus();
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
        }

        private void ToggleFlyoutWindow()
        {
            if (_flyoutWindow == null) return;

            if (_flyoutWindow.IsVisible)
            {
                _flyoutWindow.Hide();
            }
            else
            {
                RefreshMemoryNow();
                _flyoutWindow.PositionNearTray();
                _flyoutWindow.Show();
                _flyoutWindow.PositionNearTray();
                _flyoutWindow.Activate();
                _flyoutWindow.RecordShowTime();
            }
        }

        private void PurgeAllMemory()
        {
            NativeMemory.PurgeAll();
            RefreshMemoryNow();
        }

        private void PurgeWorkingSetsMemory()
        {
            NativeMemory.EmptyWorkingSets();
            RefreshMemoryNow();
        }

        private void PurgeStandbyMemory()
        {
            NativeMemory.EmptyStandbyList();
            RefreshMemoryNow();
        }

        private void LaunchRAMMapExecutable()
        {
            try
            {
                // First check if an existing RAMMap instance is already running; if so, restore and focus it
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.ProcessName.StartsWith("RAMMap", StringComparison.OrdinalIgnoreCase) &&
                            !proc.ProcessName.Equals("RAMMapTray", StringComparison.OrdinalIgnoreCase))
                        {
                            IntPtr hWnd = proc.MainWindowHandle;
                            if (hWnd != IntPtr.Zero)
                            {
                                if (IsIconic(hWnd))
                                    ShowWindowAsync(hWnd, SW_RESTORE);
                                else
                                    ShowWindowAsync(hWnd, SW_SHOW);
                                SetForegroundWindow(hWnd);
                                return;
                            }
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }

                // If not running, launch executable
                string[] candidateDirs = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Directory.GetCurrentDirectory(),
                                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools"),
                    Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName ?? "",
                    Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.Parent?.FullName ?? "",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sysinternals"),
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
