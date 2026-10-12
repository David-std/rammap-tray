using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MediaBrush = System.Windows.Media.Brush;

namespace RAMMapTray
{
    public partial class FlyoutWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern unsafe bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_ACTIVATE = 0x0006;
        private const int WA_INACTIVE = 0;
        private const int VK_SNAPSHOT = 0x2C;

        private readonly Action _onRAMMapLaunchRequest;
        private bool _isBusy = false;
        private bool _isRamMapConfirming = false;
        private bool _isScreenClippingActive = false;
        private DateTime _lastClipTriggerTime = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer? _clipWatchTimer;
        private System.Windows.Threading.DispatcherTimer? _ramMapConfirmTimer;
        private System.Windows.Threading.DispatcherTimer? _inactivityTimer;
        private DateTime _lastShowTime = DateTime.MinValue;

        public DateTime LastHideTime { get; private set; } = DateTime.MinValue;

        private IntPtr _mouseHookHandle = IntPtr.Zero;
        private LowLevelMouseProc? _mouseHookDelegate;

        private ulong _lastUsedBytes = ulong.MaxValue;
        private ulong _lastStandbyBytes = ulong.MaxValue;
        private ulong _lastFreeBytes = ulong.MaxValue;
        private MediaBrush? _cachedAccentBrush;

        private static readonly string[] DotFrames = { ".", "..", "..." };
        private static readonly System.Windows.Media.FontFamily FluentFont = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public FlyoutWindow(Action onRAMMapLaunchRequest)
        {
            InitializeComponent();
            _onRAMMapLaunchRequest = onRAMMapLaunchRequest;
            IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue)
                {
                    UninstallMouseHook();
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);

                // Prevent the window from ever appearing in the Windows Taskbar or Alt+Tab
                try
                {
                    int exStyle = GetWindowLong(source.Handle, GWL_EXSTYLE);
                    SetWindowLong(source.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
                }
                catch { }
            }
            PositionNearTray();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                int key = wParam.ToInt32();
                if (key == VK_SNAPSHOT)
                {
                    handled = true;
                    TriggerModernScreenClip();
                }
            }
            else if (msg == WM_ACTIVATE)
            {
                int state = wParam.ToInt32() & 0xFFFF;
                if (state == WA_INACTIVE)
                {
                    HandleDeactivation();
                }
            }
            return IntPtr.Zero;
        }

        public void PositionNearTray()
        {
            if (this.ActualHeight <= 0)
            {
                this.Measure(new System.Windows.Size(this.Width > 0 ? this.Width : 332, double.PositiveInfinity));
            }

            double w = this.ActualWidth > 0 ? this.ActualWidth : (this.Width > 0 ? this.Width : 332);
            double h = this.ActualHeight > 0 ? this.ActualHeight : (this.DesiredSize.Height > 0 ? this.DesiredSize.Height : 295);

            var workingArea = SystemParameters.WorkArea;
            this.Left = workingArea.Right - w - 12;
            this.Top = workingArea.Bottom - h - 12;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (sizeInfo.HeightChanged)
            {
                PositionNearTray();
            }
        }

        public void RecordShowTime()
        {
            _lastShowTime = DateTime.UtcNow;
            _isScreenClippingActive = false;
            this.Topmost = true;
            InstallMouseHook();
            StartInactivityTimer();
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && this.IsVisible)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN ||
                    msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)
                {
                    unsafe
                    {
                        MSLLHOOKSTRUCT* hookStruct = (MSLLHOOKSTRUCT*)lParam;
                        int x = hookStruct->pt.X;
                        int y = hookStruct->pt.Y;

                        IntPtr hWnd = new WindowInteropHelper(this).Handle;
                        if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rc))
                        {
                            if (x < rc.Left || x > rc.Right || y < rc.Top || y > rc.Bottom)
                            {
                                if (!_isScreenClippingActive)
                                {
                                    if (Dispatcher.CheckAccess())
                                    {
                                        HandleDeactivation();
                                    }
                                    else
                                    {
                                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(HandleDeactivation));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private void InstallMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero) return;
            try
            {
                _mouseHookDelegate = MouseHookCallback;
                IntPtr hMod = IntPtr.Zero;
                using (var curProc = Process.GetCurrentProcess())
                using (var curMod = curProc.MainModule)
                {
                    if (curMod != null)
                    {
                        hMod = GetModuleHandle(curMod.ModuleName);
                    }
                }
                _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, hMod, 0);
            }
            catch { }
        }

        private void UninstallMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(_mouseHookHandle);
                }
                catch { }
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            UninstallMouseHook();
            base.OnClosed(e);
        }

        public void StartInactivityTimer()
        {
            _inactivityTimer?.Stop();
            _inactivityTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _inactivityTimer.Tick += (s, e) =>
            {
                if (!this.IsMouseOver && !_isBusy && !_isRamMapConfirming && !_isScreenClippingActive)
                {
                    _inactivityTimer.Stop();
                    this.Hide();
                }
                else
                {
                    _inactivityTimer.Interval = TimeSpan.FromSeconds(5);
                }
            };
            _inactivityTimer.Start();
        }

        public void ResetInactivityTimer()
        {
            if (_inactivityTimer != null && _inactivityTimer.IsEnabled)
            {
                _inactivityTimer.Stop();
                _inactivityTimer.Interval = TimeSpan.FromSeconds(15);
                _inactivityTimer.Start();
            }
        }

        private void Window_UserInteraction(object sender, RoutedEventArgs e)
        {
            ResetInactivityTimer();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            ResetInactivityTimer();
            CheckAndHandleScreenClip(e);
        }

        private void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            ResetInactivityTimer();
            CheckAndHandleScreenClip(e);
        }

        private void CheckAndHandleScreenClip(System.Windows.Input.KeyEventArgs e)
        {
            bool isPrintScreen = e.Key == Key.Snapshot || 
                                 e.Key == Key.PrintScreen || 
                                 e.SystemKey == Key.Snapshot;

            bool isWinShiftS = (e.Key == Key.S || e.SystemKey == Key.S) &&
                               (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) &&
                               (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) ||
                                Keyboard.IsKeyDown(Key.LWin) ||
                                Keyboard.IsKeyDown(Key.RWin));

            if (isPrintScreen || isWinShiftS)
            {
                e.Handled = true;
                TriggerModernScreenClip();
            }
        }

        private void TriggerModernScreenClip()
        {
            if ((DateTime.UtcNow - _lastClipTriggerTime).TotalMilliseconds < 1500)
            {
                return;
            }
            _lastClipTriggerTime = DateTime.UtcNow;

            _isScreenClippingActive = true;
            this.Topmost = false;

            // Yield foreground away from the elevated window so Snipping Tool / ScreenClippingHost
            // can receive focus without UIPI interference
            try
            {
                IntPtr shellHwnd = GetShellWindow();
                if (shellHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(shellHwnd);
                }
            }
            catch { }

            // Launch ms-screenclip: via unelevated shell delegation to prevent elevated DCOM timeouts
            LaunchUnelevatedProtocol("ms-screenclip:");

            // Watch for Snipping Tool activation and completion
            _clipWatchTimer?.Stop();
            int ticks = 0;
            bool sawSnipping = false;

            _clipWatchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _clipWatchTimer.Tick += (s, args) =>
            {
                ticks++;
                bool active = IsSnippingToolActive();
                if (active) sawSnipping = true;

                if ((sawSnipping && !active) || ticks > 40)
                {
                    _clipWatchTimer?.Stop();
                    _isScreenClippingActive = false;
                    this.Topmost = true;
                    ResetInactivityTimer();
                }
            };
            _clipWatchTimer.Start();
        }

        private static void LaunchUnelevatedProtocol(string uri)
        {
            try
            {
                Type? shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                if (shellWindowsType != null)
                {
                    dynamic? shellWindows = Activator.CreateInstance(shellWindowsType);
                    if (shellWindows != null)
                    {
                        object loc = 0; // CSIDL_DESKTOP
                        object unused = new object();
                        int hwnd;
                        object? disp = shellWindows.FindWindowSW(ref loc, ref unused, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
                        if (disp != null)
                        {
                            dynamic serviceProvider = disp;
                            dynamic doc = serviceProvider.Document;
                            dynamic app = doc.Application;
                            app.ShellExecute(uri, "", "", "open", 1);
                            return;
                        }
                    }
                }
            }
            catch { }

            // Fallback: Launch via non-elevated explorer process broker
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = uri,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        public void UpdateMetrics(MemoryStatusInfo memInfo)
        {
            if (memInfo.UsedMemoryBytes == _lastUsedBytes &&
                memInfo.StandbyMemoryBytes == _lastStandbyBytes &&
                memInfo.FreeMemoryBytes == _lastFreeBytes)
            {
                return;
            }
            _lastUsedBytes = memInfo.UsedMemoryBytes;
            _lastStandbyBytes = memInfo.StandbyMemoryBytes;
            _lastFreeBytes = memInfo.FreeMemoryBytes;

            int percent = (int)Math.Round(memInfo.MemoryLoadPercentage);
            TxtPercentage.Text = percent.ToString();

            double totalGB = memInfo.TotalMemoryBytes / (1024.0 * 1024 * 1024);
            double usedGB = memInfo.UsedMemoryBytes / (1024.0 * 1024 * 1024);
            double freeGB = memInfo.FreeMemoryBytes / (1024.0 * 1024 * 1024);
            double standbyGB = memInfo.StandbyMemoryBytes / (1024.0 * 1024 * 1024);

            TxtUsedOfTotal.Text = $"{usedGB:F1} / {totalGB:F1} GB";
            TxtRamUsed.Text = $"{usedGB:F1} GB";
            TxtRamStandby.Text = standbyGB > 0 ? $"{standbyGB:F1} GB" : "0.0 GB";
            TxtRamFree.Text = $"{freeGB:F1} GB";

            // Custom rounded progress bar fill
            double clampedPercent = Math.Min(100, Math.Max(0, percent));
            double trackWidth = ProgressFill.Parent is FrameworkElement parent ? parent.ActualWidth : 280;
            if (trackWidth <= 0) trackWidth = 280;
            ProgressFill.Width = trackWidth * (clampedPercent / 100.0);

            _cachedAccentBrush ??= (MediaBrush)FindResource("AccentDefault");
            ProgressFill.Background = _cachedAccentBrush;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _clipWatchTimer?.Stop();
            _inactivityTimer?.Stop();
            ResetRAMMapButton();
            UninstallMouseHook();
            LastHideTime = DateTime.UtcNow;
            this.Hide();
        }

        private unsafe bool IsSnippingToolActive()
        {
            try
            {
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == IntPtr.Zero) return false;

                GetWindowThreadProcessId(fgHwnd, out uint pid);
                if (pid == 0) return false;

                IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProc == IntPtr.Zero) return false;

                try
                {
                    char* buf = stackalloc char[260];
                    uint size = 260;
                    if (QueryFullProcessImageNameW(hProc, 0, buf, ref size) && size > 0)
                    {
                        ReadOnlySpan<char> span = new ReadOnlySpan<char>(buf, (int)size);
                        return span.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase) ||
                               span.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                               span.Contains("ScreenClip", StringComparison.OrdinalIgnoreCase);
                    }
                }
                finally
                {
                    CloseHandle(hProc);
                }
            }
            catch { }
            return false;
        }

        private void HandleDeactivation()
        {
            if (_isScreenClippingActive)
            {
                if (IsSnippingToolActive())
                {
                    return;
                }
            }

            LastHideTime = DateTime.UtcNow;
            _inactivityTimer?.Stop();
            ResetRAMMapButton();
            UninstallMouseHook();
            this.Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            HandleDeactivation();
        }

        private async Task RunWithDotsAnimationAsync(System.Windows.Controls.Button button, string baseText, string originalText, Func<Task> action)
        {
            if (_isBusy) return;
            _isBusy = true;
            ResetInactivityTimer();

            int dotStep = 0;
            var dotsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            dotsTimer.Tick += (s, args) =>
            {
                dotStep = (dotStep % 3) + 1;
                button.Content = baseText + DotFrames[dotStep - 1];
            };
            button.Content = baseText + ".";
            dotsTimer.Start();

            try
            {
                await action();
                dotsTimer.Stop();

                if (System.Windows.Application.Current is App app) app.RefreshMemoryNow();

                button.Content = CreateSuccessContent(button.FontSize);
                await Task.Delay(1500);
            }
            catch
            {
                dotsTimer.Stop();
                button.Content = "Error";
                await Task.Delay(1500);
            }
            finally
            {
                dotsTimer.Stop();
                button.Content = originalText;
                _isBusy = false;
                ResetInactivityTimer();
            }
        }

        private static object CreateSuccessContent(double fontSize)
        {
            var sp = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            var txt = new TextBlock
            {
                Text = "Listo",
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            var icon = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = FluentFont,
                FontSize = Math.Max(9.0, fontSize - 1.0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            sp.Children.Add(txt);
            sp.Children.Add(icon);
            return sp;
        }

        private async void PurgeAll_Click(object sender, RoutedEventArgs e)
        {
            await RunWithDotsAnimationAsync(BtnOptimize, "Optimizando", "Optimizar", () => Task.Run(() => NativeMemory.PurgeAll()));
        }

        private async void PurgeWorkingSets_Click(object sender, RoutedEventArgs e)
        {
            await RunWithDotsAnimationAsync(BtnWorkingSets, "Limpiando", "Working Sets", () => Task.Run(() => NativeMemory.EmptyWorkingSets()));
        }

        private async void PurgeStandby_Click(object sender, RoutedEventArgs e)
        {
            await RunWithDotsAnimationAsync(BtnStandby, "Vaciando", "Standby List", () => Task.Run(() => NativeMemory.EmptyStandbyList()));
        }

        private async void OpenRAMMap_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            ResetInactivityTimer();

            if (!_isRamMapConfirming)
            {
                _isRamMapConfirming = true;
                BtnRAMMap.Content = "¿Abrir?";

                _ramMapConfirmTimer?.Stop();
                _ramMapConfirmTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _ramMapConfirmTimer.Tick += (s, args) =>
                {
                    _ramMapConfirmTimer?.Stop();
                    ResetRAMMapButton();
                };
                _ramMapConfirmTimer.Start();
                return;
            }

            // Confirmed launch
            _ramMapConfirmTimer?.Stop();
            _isRamMapConfirming = false;

            await RunWithDotsAnimationAsync(BtnRAMMap, "Abriendo", "RAMMap", () => Task.Run(() =>
            {
                _onRAMMapLaunchRequest?.Invoke();
            }));
        }

        private void ResetRAMMapButton()
        {
            _isRamMapConfirming = false;
            _ramMapConfirmTimer?.Stop();
            if (!_isBusy)
            {
                BtnRAMMap.Content = "RAMMap";
            }
        }
    }
}
