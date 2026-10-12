using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RAMMapTray
{
    public class TrayIconManager : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _autoStartMenuItem;
        private readonly Action _onToggleFlyout;
        private readonly Action _onOpenRAMMap;
        private readonly Action _onPurgeAll;
        private readonly Action _onPurgeWorkingSets;
        private readonly Action _onPurgeStandby;
        private int _lastPercentage = -1;
        private const string REG_APP_NAME = "RAMMapTrayCompanion";

        public TrayIconManager(
            Action onToggleFlyout, 
            Action onOpenRAMMap, 
            Action onPurgeAll,
            Action onPurgeWorkingSets,
            Action onPurgeStandby)
        {
            _onToggleFlyout = onToggleFlyout;
            _onOpenRAMMap = onOpenRAMMap;
            _onPurgeAll = onPurgeAll;
            _onPurgeWorkingSets = onPurgeWorkingSets;
            _onPurgeStandby = onPurgeStandby;

            _contextMenu = new ContextMenuStrip();
            _contextMenu.ShowImageMargin = true;
            _contextMenu.ShowCheckMargin = false;
            _contextMenu.Renderer = new DarkMenuRenderer();
            _contextMenu.Padding = new Padding(0, 4, 0, 4);

            var showItem = new ToolStripMenuItem("Abrir panel");
            showItem.Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Bold);
            showItem.Click += (s, e) => _onToggleFlyout?.Invoke();
            _contextMenu.Items.Add(showItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var purgeAllItem = new ToolStripMenuItem("Optimizar memoria");
            purgeAllItem.Font = new Font("Segoe UI Variable Text", 9f);
            purgeAllItem.Click += (s, e) => _onPurgeAll?.Invoke();
            _contextMenu.Items.Add(purgeAllItem);

            var openRamMapItem = new ToolStripMenuItem("Abrir RAMMap");
            openRamMapItem.Font = new Font("Segoe UI Variable Text", 9f);
            openRamMapItem.Click += (s, e) => _onOpenRAMMap?.Invoke();
            _contextMenu.Items.Add(openRamMapItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            _autoStartMenuItem = new ToolStripMenuItem("Iniciar con Windows");
            _autoStartMenuItem.Font = new Font("Segoe UI Variable Text", 9f);
            _autoStartMenuItem.Click += (s, e) => ToggleAutoStart();
            UpdateAutoStartIcon();
            _contextMenu.Items.Add(_autoStartMenuItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Salir");
            exitItem.Font = new Font("Segoe UI Variable Text", 9f);
            exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();
            _contextMenu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                ContextMenuStrip = _contextMenu,
                Text = "RAM"
            };

            DateTime lastClickTime = DateTime.MinValue;
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if ((DateTime.UtcNow - lastClickTime).TotalMilliseconds < 250)
                    {
                        return;
                    }
                    lastClickTime = DateTime.UtcNow;
                    _onToggleFlyout?.Invoke();
                }
            };

            UpdateIcon(50, "RAM: 50%");
            _notifyIcon.Visible = true;
        }

        private static readonly Icon?[] _iconCache = new Icon?[101];

        public void UpdateIcon(int ramPercentage, string tooltipText)
        {
            try
            {
                string safeTooltip = tooltipText.Length > 63 ? tooltipText.Substring(0, 63) : tooltipText;
                _notifyIcon.Text = safeTooltip;

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

        private const string TASK_NAME = "RAMMapTrayCompanion";

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

        private void UpdateAutoStartIcon()
        {
            _autoStartMenuItem.Image = IsAutoStartEnabled()
                ? RenderIcon("\uE73E", Color.White)
                : null;
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

                UpdateAutoStartIcon();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cambiar inicio automático: {ex.Message}", "RAMMap", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static Bitmap RenderIcon(string glyph, Color color, int size = 16)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (Font f = new Font("Segoe MDL2 Assets", 10f, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush b = new SolidBrush(color))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(glyph, f, b, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Icon?.Dispose();
                _notifyIcon.Dispose();
                _contextMenu?.Dispose();
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

    /// <summary>Custom dark renderer for ContextMenuStrip to match WinUI 3 dark flyout style.</summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color BgColor = Color.FromArgb(44, 44, 44);
        private static readonly Color BorderColor = Color.FromArgb(56, 56, 56);
        private static readonly Color HoverColor = Color.FromArgb(62, 62, 62);
        private static readonly Color SepColor = Color.FromArgb(56, 56, 56);
        private static readonly Color TextColor = Color.FromArgb(235, 235, 235);

        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(BgColor))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(BorderColor))
                e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using (SolidBrush b = new SolidBrush(HoverColor))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = RoundedRect(rect, 4))
                        e.Graphics.FillPath(b, path);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = TextColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(SepColor))
                e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No background for image column
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuBorder => BorderColor;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => HoverColor;
            public override Color MenuStripGradientBegin => BgColor;
            public override Color MenuStripGradientEnd => BgColor;
            public override Color ToolStripDropDownBackground => BgColor;
            public override Color ImageMarginGradientBegin => BgColor;
            public override Color ImageMarginGradientMiddle => BgColor;
            public override Color ImageMarginGradientEnd => BgColor;
            public override Color SeparatorDark => SepColor;
            public override Color SeparatorLight => Color.Transparent;
        }
    }
}
