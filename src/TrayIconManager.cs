using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RAMMapTray
{
    public class TrayIconManager : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int appMode);

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MF_CHECKED = 0x00000008;
        private const uint MF_UNCHECKED = 0x00000000;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint WM_NULL = 0x0000;

        private const uint CMD_OPEN_FLYOUT = 1001;
        private const uint CMD_OPTIMIZE = 1002;
        private const uint CMD_OPEN_RAMMAP = 1003;
        private const uint CMD_AUTO_START = 1004;
        private const uint CMD_EXIT = 1005;

        private readonly NotifyIcon _notifyIcon;
        private readonly Func<IntPtr> _getOwnerHwnd;
        private readonly Action _onToggleFlyout;
        private readonly Action _onOpenRAMMap;
        private readonly Action _onPurgeAll;
        private readonly Action _onPurgeWorkingSets;
        private readonly Action _onPurgeStandby;
        private int _lastPercentage = -1;
        private const string REG_APP_NAME = "RAMMapTrayCompanion";
        private const string TASK_NAME = "RAMMapTrayCompanion";

        public TrayIconManager(
            Func<IntPtr> getOwnerHwnd,
            Action onToggleFlyout, 
            Action onOpenRAMMap, 
            Action onPurgeAll,
            Action onPurgeWorkingSets,
            Action onPurgeStandby)
        {
            _getOwnerHwnd = getOwnerHwnd;
            _onToggleFlyout = onToggleFlyout;
            _onOpenRAMMap = onOpenRAMMap;
            _onPurgeAll = onPurgeAll;
            _onPurgeWorkingSets = onPurgeWorkingSets;
            _onPurgeStandby = onPurgeStandby;

            _notifyIcon = new NotifyIcon
            {
                Text = "RAM"
            };

            DateTime lastClickTime = DateTime.MinValue;
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if ((DateTime.UtcNow - lastClickTime).TotalMilliseconds < 200)
                    {
                        return;
                    }
                    lastClickTime = DateTime.UtcNow;
                    _onToggleFlyout?.Invoke();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    ShowNativeContextMenu();
                }
            };

            UpdateIcon(50, "RAM: 50%");
            _notifyIcon.Visible = true;
        }

        private void ShowNativeContextMenu()
        {
            try
            {
                // Force dark mode for Win32 popup menus
                try { SetPreferredAppMode(2); } catch { }

                IntPtr hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                try
                {
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_OPEN_FLYOUT, "Abrir panel");
                    SetMenuDefaultItem(hMenu, CMD_OPEN_FLYOUT, 0);

                    AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);

                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_OPTIMIZE, "Optimizar memoria");
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_OPEN_RAMMAP, "Abrir RAMMap");

                    AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);

                    bool autoStart = IsAutoStartEnabled();
                    uint checkFlag = autoStart ? MF_CHECKED : MF_UNCHECKED;
                    AppendMenuW(hMenu, MF_STRING | checkFlag, (UIntPtr)CMD_AUTO_START, "Iniciar con Windows");

                    AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);

                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_EXIT, "Salir");

                    GetCursorPos(out POINT pt);

                    IntPtr ownerHwnd = _getOwnerHwnd();
                    if (ownerHwnd != IntPtr.Zero)
                    {
                        SetForegroundWindow(ownerHwnd);
                    }

                    int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, ownerHwnd, IntPtr.Zero);

                    if (ownerHwnd != IntPtr.Zero)
                    {
                        PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                    }

                    switch (cmd)
                    {
                        case (int)CMD_OPEN_FLYOUT:
                            _onToggleFlyout?.Invoke();
                            break;
                        case (int)CMD_OPTIMIZE:
                            _onPurgeAll?.Invoke();
                            break;
                        case (int)CMD_OPEN_RAMMAP:
                            _onOpenRAMMap?.Invoke();
                            break;
                        case (int)CMD_AUTO_START:
                            ToggleAutoStart();
                            break;
                        case (int)CMD_EXIT:
                            System.Windows.Application.Current.Shutdown();
                            break;
                    }
                }
                finally
                {
                    DestroyMenu(hMenu);
                }
            }
            catch { }
        }

        private static readonly Icon?[] _iconCache = new Icon?[101];

        public void UpdateIcon(int ramPercentage, string tooltipText)
        {
            try
            {
                string safeTooltip = tooltipText.Length > 63 ? tooltipText.Substring(0, 63) : tooltipText;
                if (_notifyIcon.Text != safeTooltip)
                {
                    _notifyIcon.Text = safeTooltip;
                }

                if (ramPercentage == _lastPercentage && _notifyIcon.Icon != null)
                {
                    return;
                }

                _lastPercentage = ramPercentage;
                int clamped = Math.Max(0, Math.Min(100, ramPercentage));

                Icon? cached = _iconCache[clamped];
                if (cached == null)
                {
                    using (Bitmap bmp = CreateWaterGlassIcon(clamped))
                    {
                        IntPtr hIcon = bmp.GetHicon();
                        try
                        {
                            using (Icon tempIcon = Icon.FromHandle(hIcon))
                            {
                                cached = (Icon)tempIcon.Clone();
                                _iconCache[clamped] = cached;
                            }
                        }
                        finally
                        {
                            DestroyIcon(hIcon);
                        }
                    }
                }

                _notifyIcon.Icon = cached;
            }
            catch
            {
                if (_notifyIcon.Icon == null)
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
        }

        private Bitmap CreateWaterGlassIcon(int percentage)
        {
            int size = 32;
            Bitmap bmp = new Bitmap(size, size);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // Glass container bounds
                int glassX = 7;
                int glassY = 4;
                int glassW = 18;
                int glassH = 24;
                int radius = 4;

                // Color based on water level
                Color waterColor;
                if (percentage < 70)
                    waterColor = Color.FromArgb(56, 189, 248);  // Clean Sky Blue (water)
                else if (percentage < 88)
                    waterColor = Color.FromArgb(251, 191, 36);  // Amber
                else
                    waterColor = Color.FromArgb(248, 113, 113); // Coral Red

                // 1. Draw Water inside the glass (clipped to glass shape)
                using (GraphicsPath glassPath = CreateGlassPath(glassX, glassY, glassW, glassH, radius))
                {
                    g.SetClip(glassPath);

                    int fillH = (int)Math.Round((glassH - 2) * (percentage / 100.0));
                    fillH = Math.Max(2, Math.Min(glassH, fillH));
                    int fillY = glassY + glassH - fillH;

                    using (SolidBrush waterBrush = new SolidBrush(waterColor))
                    {
                        g.FillRectangle(waterBrush, glassX, fillY, glassW, fillH);
                    }

                    // Water surface line
                    using (Pen surfacePen = new Pen(Color.FromArgb(220, 255, 255, 255), 1f))
                    {
                        g.DrawLine(surfacePen, glassX + 1, fillY, glassX + glassW - 1, fillY);
                    }

                    g.ResetClip();
                }

                // 2. Draw Glass Outline (Glass Contour)
                using (GraphicsPath glassPath = CreateGlassPath(glassX, glassY, glassW, glassH, radius))
                using (Pen glassPen = new Pen(Color.FromArgb(230, 240, 245), 2f))
                {
                    g.DrawPath(glassPen, glassPath);
                }

                // 3. Subtle glass lip (top opening ticks)
                using (Pen lipPen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f))
                {
                    g.DrawLine(lipPen, glassX - 1, glassY, glassX + 2, glassY);
                    g.DrawLine(lipPen, glassX + glassW - 2, glassY, glassX + glassW + 1, glassY);
                }
            }

            return bmp;
        }

        private static GraphicsPath CreateGlassPath(int x, int y, int width, int height, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;

            // Top left to bottom left
            path.AddLine(x, y, x, y + height - radius);
            // Bottom left arc
            path.AddArc(x, y + height - diameter, diameter, diameter, 180, -90);
            // Bottom line
            path.AddLine(x + radius, y + height, x + width - radius, y + height);
            // Bottom right arc
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 90, -90);
            // Bottom right to top right
            path.AddLine(x + width, y + height - radius, x + width, y);

            return path;
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\{TASK_NAME}"))
                {
                    if (key != null) return true;
                }
            }
            catch { }

            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key?.GetValue(REG_APP_NAME) != null) return true;
                }
            }
            catch { }

            return false;
        }

        public void ToggleAutoStart()
        {
            try
            {
                // Clean legacy Registry Run entry if present
                try
                {
                    using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        key?.DeleteValue(REG_APP_NAME, false);
                    }
                }
                catch { }

                bool isCurrentlyEnabled = IsAutoStartEnabled();

                if (isCurrentlyEnabled)
                {
                    using var p = new System.Diagnostics.Process();
                    p.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Delete /TN \"{TASK_NAME}\" /F",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    p.Start();
                    p.WaitForExit(3000);
                }
                else
                {
                    string? exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        using var p = new System.Diagnostics.Process();
                        p.StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "schtasks.exe",
                            Arguments = $"/Create /TN \"{TASK_NAME}\" /TR \"'{exePath}'\" /SC ONLOGON /RL HIGHEST /F",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        p.Start();
                        string err = p.StandardError.ReadToEnd();
                        p.WaitForExit(3000);

                        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                        {
                            MessageBox.Show($"No se pudo crear la tarea de inicio automático:\n{err}", "RAMMap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cambiar inicio automático: {ex.Message}", "RAMMap", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Icon?.Dispose();
                _notifyIcon.Dispose();
                for (int i = 0; i < _iconCache.Length; i++)
                {
                    _iconCache[i]?.Dispose();
                    _iconCache[i] = null;
                }
            }
            catch
            {
            }
        }
    }
}
