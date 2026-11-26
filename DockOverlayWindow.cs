using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskbarDock {
    /// <summary>
    /// Smooth floating Quick Settings / Dock Menu button on the right side of the dock capsule.
    /// </summary>
    public class DockOverlayWindow : Form {
        private Config _config;
        private Action _onOpenSettings;
        private bool _isHovered = false;
        private bool _isPressed = false;
        private bool _regionSet = false;
        private int _baseBtnX = 0;
        private int _baseBtnY = 0;
        private const int BtnSize = 26;

        protected override CreateParams CreateParams {
            get {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation {
            get { return true; }
        }

        public DockOverlayWindow(Config config, Action onOpenSettings) {
            _config = config;
            _onOpenSettings = onOpenSettings;

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.Size = new Size(BtnSize, BtnSize);
            this.DoubleBuffered = true;
            // Match exact dock capsule background so AntiAlias circle edge blends seamlessly without jagged SetWindowRgn
            this.BackColor = Color.FromArgb(24, 24, 26);

            this.MouseEnter += (s, e) => { _isHovered = true; this.Invalidate(); };
            this.MouseLeave += (s, e) => { _isHovered = false; _isPressed = false; this.Invalidate(); };
            this.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) {
                    _isPressed = true;
                    this.Invalidate();
                }
            };
            this.MouseUp += (s, e) => {
                if (_isPressed) {
                    _isPressed = false;
                    this.Invalidate();
                    ShowMenu();
                }
            };
            this.DoubleClick += (s, e) => {
                if (_onOpenSettings != null) _onOpenSettings();
            };
        }

        public void UpdateOverlay(int left, int top, int right, int bottom, Config config) {
            _config = config;
            if (!_config.ShowQuickSettingsButton || !_config.Enabled) {
                if (this.Visible) this.Hide();
                return;
            }

            int visualMargin = Math.Max(12, _config.PaddingHorizontal);
            int btnX = right - visualMargin - BtnSize;
            int btnY = top + (bottom - top - BtnSize) / 2;
            _baseBtnX = btnX;
            _baseBtnY = btnY;

            this.Location = new Point(btnX, btnY);
            this.Size = new Size(BtnSize, BtnSize);

            if (!_regionSet) {
                IntPtr rgn = NativeMethods.CreateRoundRectRgn(0, 0, BtnSize + 1, BtnSize + 1, BtnSize + 1, BtnSize + 1);
                NativeMethods.SetWindowRgn(this.Handle, rgn, true);
                _regionSet = true;
            }

            if (!this.Visible) {
                this.Show();
            }

            NativeMethods.SetWindowPos(this.Handle, NativeMethods.HWND_TOPMOST, btnX, btnY, BtnSize, BtnSize, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            this.Invalidate();
        }

        public void SetVerticalOffset(int offsetY) {
            if (_baseBtnX == 0 && _baseBtnY == 0) return;
            int y = _baseBtnY + offsetY;
            NativeMethods.SetWindowPos(this.Handle, NativeMethods.HWND_TOPMOST, _baseBtnX, y, BtnSize, BtnSize, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
        }

        public void SetButtonOpacity(double opacity) {
            if (opacity < 0) opacity = 0;
            if (opacity > 1) opacity = 1;
            this.Opacity = opacity;
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Color bg = _isPressed
                ? Color.FromArgb(255, 72, 72, 86)
                : (_isHovered ? Color.FromArgb(255, 56, 56, 68) : Color.FromArgb(255, 38, 38, 46));
            using (SolidBrush b = new SolidBrush(bg)) {
                g.FillEllipse(b, 2.0f, 2.0f, BtnSize - 4.0f, BtnSize - 4.0f);
            }

            Color border = _isHovered ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255);
            using (Pen p = new Pen(border, 1.0f)) {
                g.DrawEllipse(p, 2.0f, 2.0f, BtnSize - 4.0f, BtnSize - 4.0f);
            }

            float cx = BtnSize / 2.0f;
            float cy = BtnSize / 2.0f;
            using (Pen p = new Pen(Color.FromArgb(245, 245, 250), 1.4f)) {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;

                g.DrawLine(p, cx - 5.5f, cy - 4.0f, cx + 5.5f, cy - 4.0f);
                g.FillEllipse(Brushes.White, cx - 3.5f, cy - 5.75f, 3.5f, 3.5f);

                g.DrawLine(p, cx - 5.5f, cy, cx + 5.5f, cy);
                g.FillEllipse(Brushes.White, cx + 0.5f, cy - 1.75f, 3.5f, 3.5f);

                g.DrawLine(p, cx - 5.5f, cy + 4.0f, cx + 5.5f, cy + 4.0f);
                g.FillEllipse(Brushes.White, cx - 2.0f, cy + 2.25f, 3.5f, 3.5f);
            }
        }

        public static void TriggerQuickSettings() {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) NativeMethods.SetForegroundWindow(tray);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_A, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_A, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void TriggerNotificationCenter() {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) NativeMethods.SetForegroundWindow(tray);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_N, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_N, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void TriggerTrayOverflow() {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) NativeMethods.SetForegroundWindow(tray);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_B, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_B, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void ShowMenu() {
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("⚙  Настройки TaskbarDock...", (s, e) => {
                if (_onOpenSettings != null) _onOpenSettings();
            });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("🔊  Быстрые настройки (Звук, Wi-Fi, Батарея) [Win+A]", (s, e) => TriggerQuickSettings());
            menu.MenuItems.Add("🔔  Центр уведомлений и календарь [Win+N]", (s, e) => TriggerNotificationCenter());
            menu.MenuItems.Add("🔺  Скрытые значки трея [Win+B]", (s, e) => TriggerTrayOverflow());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("✖  Восстановить панель задач", (s, e) => {
                _config.Enabled = false;
                _config.Save();
            });

            NativeMethods.SetForegroundWindow(this.Handle);
            menu.Show(this, new Point(0, -menu.MenuItems.Count * 22));
        }
    }
}
