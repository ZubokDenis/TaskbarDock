using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskbarDock {
    /// <summary>
    /// Crisp WPF DirectWrite minimalist dark matte tray strip displayed directly above the dock.
    /// </summary>
    internal class DockTrayBarWindow : Window {
        private enum BarItemKind {
            TrayOverflow,
            PinnedApp,
            Language,
            Network,
            Volume,
            Battery,
            Clock,
            Settings
        }

        private class TraySnapshot {
            public string OverflowName = "";
            public string InputName = "";
            public string InputCode = "RUS";
            public string NetworkName = "";
            public bool NetworkConnected = true;
            public bool NetworkWired = false;
            public int NetworkSignal = 85;
            public string VolumeName = "";
            public int VolumePercent = 50;
            public bool VolumeMuted = false;
            public string BatteryName = "";
            public List<string> PinnedNames = new List<string>();
        }

        private Config _config;
        private Action _onOpenSettings;
        private TraySnapshot _snapshot = new TraySnapshot();
        private readonly object _snapLock = new object();

        private Border _rootBorder;
        private StackPanel _itemsHost;
        private TranslateTransform _slideTransform;
        private DockFlyoutWindow _flyout;

        private TextBlock _langText;
        private Path _wifiPath;
        private Path _volPath;
        private TextBlock _batPctText;
        private Rectangle _batFillRect;
        private TextBlock _clockTimeText;
        private TextBlock _clockDateText;
        private Border _overflowTile;
        private Dictionary<FlyoutKind, Border> _flyoutTiles = new Dictionary<FlyoutKind, Border>();

        private DispatcherTimer _clockTimer;
        private DispatcherTimer _overflowMonitorTimer;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _prevForegroundHwnd = IntPtr.Zero;
        private long _keepOpenForTrayOverflowUntil = 0;
        private long _lastClosedTick = 0;
        private bool _isClosingAnim = false;
        private int _refreshSeq = 0;

        private int _dockLeft;
        private int _dockTop;
        private int _dockRight;
        private int _dockBottom;

        public volatile bool IsOpenVolatile = false;
        public volatile IntPtr HwndVolatile = IntPtr.Zero;
        public volatile IntPtr FlyoutHwndVolatile = IntPtr.Zero;
        public long LastClosedTick { get { return _lastClosedTick; } }
        public IntPtr Hwnd { get { return _hwnd; } }

        public DockTrayBarWindow(Config config, Action onOpenSettings) {
            _config = config;
            _onOpenSettings = onOpenSettings;

            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.ShowInTaskbar = false;
            this.Topmost = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.SizeToContent = SizeToContent.WidthAndHeight;
            this.UseLayoutRounding = true;
            this.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);

            _slideTransform = new TranslateTransform(0, 7);
            _itemsHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _rootBorder = new Border {
                Child = _itemsHost,
                Height = 40,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(1),
                RenderTransform = _slideTransform
            };
            this.Content = _rootBorder;

            _flyout = new DockFlyoutWindow(this, _config, () => {
                ReadLocalAudioAndLang();
                UpdateLiveVisuals();
            });

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => {
                if (this.IsVisible && !_isClosingAnim) {
                    UpdateClockVisuals();
                }
            };

            _overflowMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _overflowMonitorTimer.Tick += (s, e) => {
                if (!this.IsVisible) {
                    _overflowMonitorTimer.Stop();
                    return;
                }
                long now = Environment.TickCount & 0x7FFFFFFF;
                if (now < _keepOpenForTrayOverflowUntil) return;
                if (IsAnyOverflowWindowVisible()) return;
                _overflowMonitorTimer.Stop();
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != _hwnd && (_flyout == null || fg != _flyout.Hwnd)) {
                    HideBar(immediate: true);
                }
            };

            this.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Escape) {
                    if (_flyout != null) _flyout.HideFlyout(animate: false);
                    HideBar(immediate: false);
                    e.Handled = true;
                }
            };
        }

        public void EnsurePrewarmed() {
            if (_hwnd == IntPtr.Zero) {
                var helper = new WindowInteropHelper(this);
                _hwnd = helper.EnsureHandle();
                HwndVolatile = _hwnd;
                int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
                HwndSource src = HwndSource.FromHwnd(_hwnd);
                if (src != null) src.AddHook(WndProcHook);
            }
            _flyout.EnsureHandleCreated();
            FlyoutHwndVolatile = _flyout.Hwnd;

            // Pre-build items & prime UIA snapshot in background so first open is instant and never shifts width
            ReadForegroundInputLanguage();
            ReadLocalAudioAndLang();
            ApplyDockTheme();
            BuildStripItems();
            RefreshTraySnapshotAsync(rebuildLayout: false);
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == NativeMethods.WM_ACTIVATE) {
                int state = wParam.ToInt32() & 0xFFFF;
                if (state == 0 && !_isClosingAnim && this.IsVisible) {
                    IntPtr activating = lParam;
                    long now = Environment.TickCount & 0x7FFFFFFF;
                    if (_flyout != null && activating == _flyout.Hwnd) {
                        return IntPtr.Zero;
                    }
                    if (now < _keepOpenForTrayOverflowUntil || IsOverflowWindow(activating) || IsAnyOverflowWindowVisible()) {
                        _overflowMonitorTimer.Start();
                        return IntPtr.Zero;
                    }
                    HideBar(immediate: true);
                }
            }
            return IntPtr.Zero;
        }

        public void ApplyLanguageLocally(string code, string displayName) {
            lock (_snapLock) {
                _snapshot.InputCode = code;
                _snapshot.InputName = displayName;
            }
            UpdateLiveVisuals();
        }

        public void ShowAboveDock(int dockLeft, int dockTop, int dockRight, int dockBottom, Config config) {
            EnsurePrewarmed();
            _config = config ?? _config;
            _dockLeft = dockLeft;
            _dockTop = dockTop;
            _dockRight = dockRight;
            _dockBottom = dockBottom;
            _isClosingAnim = false;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _hwnd && (_flyout == null || fg != _flyout.Hwnd)) {
                _prevForegroundHwnd = fg;
            }

            ReadForegroundInputLanguage();
            ReadLocalAudioAndLang();
            ApplyDockTheme();
            BuildStripItems();
            UpdateLiveVisuals();

            this.UpdateLayout();
            PositionAboveDock();

            IsOpenVolatile = true;
            if (!this.IsVisible) {
                this.Opacity = 0;
                _slideTransform.Y = 7;
                this.Show();
                PositionAboveDock();

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var slide = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(170)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(Window.OpacityProperty, fade);
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
            }

            NativeMethods.SetForegroundWindow(_hwnd);
            this.Activate();

            _clockTimer.Start();
            RefreshTraySnapshotAsync(rebuildLayout: false);
        }

        public void UpdateDockAnchor(int dockLeft, int dockTop, int dockRight, int dockBottom, Config config) {
            _config = config ?? _config;
            _dockLeft = dockLeft;
            _dockTop = dockTop;
            _dockRight = dockRight;
            _dockBottom = dockBottom;
            if (this.IsVisible && !_isClosingAnim) {
                ApplyDockTheme();
                PositionAboveDock();
            }
        }

        public void HideBar(bool immediate) {
            if (_flyout != null) _flyout.HideFlyout(animate: false);
            _overflowMonitorTimer.Stop();
            _clockTimer.Stop();
            _keepOpenForTrayOverflowUntil = 0;
            _lastClosedTick = Environment.TickCount & 0x7FFFFFFF;

            if (!this.IsVisible) {
                IsOpenVolatile = false;
                return;
            }

            if (immediate) {
                _isClosingAnim = false;
                IsOpenVolatile = false;
                this.BeginAnimation(Window.OpacityProperty, null);
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                this.Hide();
                return;
            }

            if (_isClosingAnim) return;
            _isClosingAnim = true;
            var fade = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(110)) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slide = new DoubleAnimation(0, 5, TimeSpan.FromMilliseconds(110)) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, e) => {
                if (_isClosingAnim) {
                    _isClosingAnim = false;
                    IsOpenVolatile = false;
                    this.Hide();
                }
            };
            this.BeginAnimation(Window.OpacityProperty, fade);
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void ApplyDockTheme() {
            byte bgAlpha = (_config != null) ? (byte)Math.Max(140, Math.Min(255, _config.TaskbarOpacity)) : (byte)235;
            double radius = (_config != null) ? Math.Min(20.0, Math.Max(4.0, _config.CornerRadius)) : 16.0;
            _rootBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 24, 24, 26));
            _rootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            _rootBorder.CornerRadius = new CornerRadius(radius);
        }

        private void PositionAboveDock() {
            this.UpdateLayout();
            double w = this.ActualWidth > 0 ? this.ActualWidth : this.DesiredSize.Width;
            double h = 40;
            var pb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

            double centerX = (_dockRight > _dockLeft) ? ((_dockLeft + _dockRight) / 2.0) : (pb.Left + pb.Width / 2.0);
            double left = centerX - w / 2.0;
            if (left < pb.Left + 10) left = pb.Left + 10;
            if (left + w > pb.Right - 10) left = pb.Right - 10 - w;

            double dockTop = (_dockTop > 0) ? _dockTop : (pb.Bottom - 48);
            double top = dockTop - 10 - h;

            this.Left = Math.Round(left);
            this.Top = Math.Round(top);
        }

        private void BuildStripItems() {
            _itemsHost.Children.Clear();
            _flyoutTiles.Clear();

            TraySnapshot snap;
            lock (_snapLock) { snap = _snapshot; }

            // 1. Hidden Tray Overflow (^)
            var chevron = new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 12,
                Height = 12,
                Data = Geometry.Parse("M 1.5,8.5 L 6,4 L 10.5,8.5")
            };
            _overflowTile = CreateStripButton(chevron, 28, "Скрытые значки трея", null, () => HandleOverflowClick());
            _itemsHost.Children.Add(_overflowTile);

            // Optional pinned apps
            foreach (string pinned in snap.PinnedNames) {
                string shortCh = "•";
                foreach (char c in pinned) {
                    if (char.IsLetterOrDigit(c)) { shortCh = char.ToUpperInvariant(c).ToString(); break; }
                }
                string pName = pinned;
                var badge = new Border {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(40, 120, 180, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(100, 150, 200, 255)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock {
                        Text = shortCh,
                        Foreground = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                _itemsHost.Children.Add(CreateStripButton(badge, 28, FormatTooltip(pName), null, () => InvokePinnedApp(pName)));
            }

            AddSeparator();

            // 2. Language (RUS / ENG)
            _langText = new TextBlock {
                Text = snap.InputCode,
                Foreground = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var langBtn = CreateStripButton(_langText, 36, "Раскладка клавиатуры", null, () => ToggleFlyoutFor(FlyoutKind.Language));
            _flyoutTiles[FlyoutKind.Language] = langBtn;
            _itemsHost.Children.Add(langBtn);

            // 3. Wi-Fi / Network
            _wifiPath = new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                StrokeThickness = 1.45,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 14,
                Stretch = Stretch.Uniform
            };
            var netBtn = CreateStripButton(_wifiPath, 30, string.IsNullOrEmpty(snap.NetworkName) ? "Сеть Wi-Fi" : FormatTooltip(snap.NetworkName), null, () => ToggleFlyoutFor(FlyoutKind.Network));
            _flyoutTiles[FlyoutKind.Network] = netBtn;
            _itemsHost.Children.Add(netBtn);

            // 4. Volume
            _volPath = new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                StrokeThickness = 1.45,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 15,
                Stretch = Stretch.Uniform
            };
            var volBtn = CreateStripButton(_volPath, 30, "Громкость (колесо — регулировка)", (s, e) => {
                int pct; bool muted;
                if (SystemAudio.GetVolumeState(out pct, out muted)) {
                    int next = Math.Max(0, Math.Min(100, pct + (e.Delta > 0 ? 4 : -4)));
                    SystemAudio.SetVolumePercent(next);
                    ReadLocalAudioAndLang();
                    UpdateLiveVisuals();
                }
            }, () => ToggleFlyoutFor(FlyoutKind.Volume));
            _flyoutTiles[FlyoutKind.Volume] = volBtn;
            _itemsHost.Children.Add(volBtn);

            // 5. Battery
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0) {
                var batRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var batGrid = new Grid { Width = 20, Height = 11, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                var shell = new Border {
                    Width = 17,
                    Height = 10,
                    CornerRadius = new CornerRadius(2),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(232, 236, 244)),
                    BorderThickness = new Thickness(1.2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                _batFillRect = new Rectangle {
                    Height = 6,
                    RadiusX = 1,
                    RadiusY = 1,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(1.2, 0, 0, 0)
                };
                shell.Child = _batFillRect;
                var tip = new Rectangle {
                    Width = 2,
                    Height = 4,
                    Fill = new SolidColorBrush(Color.FromRgb(232, 236, 244)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                batGrid.Children.Add(shell);
                batGrid.Children.Add(tip);
                _batPctText = new TextBlock {
                    Foreground = new SolidColorBrush(Color.FromRgb(224, 228, 238)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                batRow.Children.Add(batGrid);
                batRow.Children.Add(_batPctText);

                var batBtn = CreateStripButton(batRow, 54, "Батарея", null, () => ToggleFlyoutFor(FlyoutKind.Battery));
                _flyoutTiles[FlyoutKind.Battery] = batBtn;
                _itemsHost.Children.Add(batBtn);
            }

            AddSeparator();

            // 6. Date & Time
            var clockStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _clockTimeText = new TextBlock {
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                LineHeight = 13
            };
            _clockDateText = new TextBlock {
                Foreground = new SolidColorBrush(Color.FromRgb(166, 173, 188)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
                LineHeight = 11
            };
            clockStack.Children.Add(_clockTimeText);
            clockStack.Children.Add(_clockDateText);
            var clockBtn = CreateStripButton(clockStack, 68, "Дата и время", null, () => ToggleFlyoutFor(FlyoutKind.Calendar));
            _flyoutTiles[FlyoutKind.Calendar] = clockBtn;
            _itemsHost.Children.Add(clockBtn);

            AddSeparator();

            // 7. Settings gear
            var gear = new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                StrokeThickness = 1.45,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 14,
                Height = 14,
                Data = Geometry.Parse("M 7,4.2 A 2.8,2.8 0 1 1 6.99,4.2 M 7,0.8 L 7,2.5 M 7,11.5 L 7,13.2 M 0.8,7 L 2.5,7 M 11.5,7 L 13.2,7 M 2.6,2.6 L 3.8,3.8 M 10.2,10.2 L 11.4,11.4 M 11.4,2.6 L 10.2,3.8 M 3.8,10.2 L 2.6,11.4")
            };
            _itemsHost.Children.Add(CreateStripButton(gear, 28, "Настройки TaskbarDock", null, () => {
                HideBar(immediate: true);
                if (_onOpenSettings != null) _onOpenSettings();
            }));
        }

        private void AddSeparator() {
            _itemsHost.Children.Add(new Border {
                Width = 1,
                Height = 18,
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        private Border CreateStripButton(UIElement content, double width, string tooltip, MouseWheelEventHandler onWheel, Action onClick) {
            var b = new Border {
                Child = content,
                Width = width,
                Height = 30,
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrEmpty(tooltip) ? null : CreateDarkTooltip(tooltip)
            };
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) => b.Background = new SolidColorBrush(Color.FromArgb(44, 255, 255, 255));
            b.MouseLeftButtonUp += (s, e) => {
                b.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
                if (onClick != null) onClick();
            };
            if (onWheel != null) b.MouseWheel += onWheel;
            return b;
        }

        private static ToolTip CreateDarkTooltip(string text) {
            return new ToolTip {
                Content = text,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
                Foreground = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5
            };
        }

        private void UpdateLiveVisuals() {
            TraySnapshot snap;
            lock (_snapLock) { snap = _snapshot; }

            if (_langText != null) _langText.Text = string.IsNullOrEmpty(snap.InputCode) ? "RUS" : snap.InputCode;
            if (_wifiPath != null) _wifiPath.Data = DockFlyoutWindow.BuildWifiGeometry(snap.NetworkSignal, snap.NetworkConnected);
            if (_volPath != null) _volPath.Data = DockFlyoutWindow.BuildSpeakerGeometry(snap.VolumeMuted || snap.VolumePercent == 0, snap.VolumePercent);

            if (_batPctText != null && _batFillRect != null) {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                int pct = Math.Max(0, Math.Min(100, (int)Math.Round(ps.BatteryLifePercent * 100)));
                bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                _batPctText.Text = pct + "%";
                _batFillRect.Width = Math.Max(2, 13.0 * (pct / 100.0));
                _batFillRect.Fill = new SolidColorBrush(charging ? Color.FromRgb(95, 215, 130) : (pct <= 20 ? Color.FromRgb(255, 105, 97) : Color.FromRgb(236, 238, 246)));
            }

            UpdateClockVisuals();
        }

        private void UpdateClockVisuals() {
            DateTime now = DateTime.Now;
            if (_clockTimeText != null) _clockTimeText.Text = now.ToString("HH:mm");
            if (_clockDateText != null) _clockDateText.Text = now.ToString("dd.MM.yyyy");
        }

        private void ToggleFlyoutFor(FlyoutKind fk) {
            if (_flyout == null) return;
            if (_flyout.IsVisible && _flyout.CurrentKind == fk) {
                _flyout.HideFlyout(animate: true);
                return;
            }
            Border tile;
            int centerX = (int)Math.Round(this.Left + this.ActualWidth / 2.0);
            if (_flyoutTiles.TryGetValue(fk, out tile) && tile != null) {
                Point p = tile.TranslatePoint(new Point(tile.ActualWidth / 2.0, 0), this);
                centerX = (int)Math.Round(this.Left + p.X);
            }
            int anchorBottomY = (int)Math.Round(this.Top - 8);
            string activeCode;
            lock (_snapLock) { activeCode = _snapshot.InputCode; }
            _flyout.ShowFlyout(fk, centerX, anchorBottomY, _config, _prevForegroundHwnd, activeCode);
        }

        private void HandleOverflowClick() {
            if (_flyout != null) _flyout.HideFlyout(animate: false);

            long now = Environment.TickCount & 0x7FFFFFFF;
            _keepOpenForTrayOverflowUntil = now + 900;
            _overflowMonitorTimer.Start();

            int centerX = (int)Math.Round(this.Left + 22);
            if (_overflowTile != null) {
                Point p = _overflowTile.TranslatePoint(new Point(_overflowTile.ActualWidth / 2.0, 0), this);
                centerX = (int)Math.Round(this.Left + p.X);
            }
            int anchorBottomY = (int)Math.Round(this.Top - 8);
            string exactName;
            lock (_snapLock) { exactName = _snapshot.OverflowName; }

            bool wasOpen = IsAnyOverflowWindowVisible();
            Thread t = new Thread(() => {
                IntPtr hiddenOverflow = IntPtr.Zero;
                try {
                    IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero) NativeMethods.SetThreadDesktop(hDesk);

                    // Pre-clip overflow window to 0x0 before Invoke so it NEVER flashes on the right edge!
                    if (!wasOpen) {
                        hiddenOverflow = FindOverflowWindowHandle();
                        if (hiddenOverflow != IntPtr.Zero) {
                            IntPtr emptyRgn = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                            NativeMethods.SetWindowRgn(hiddenOverflow, emptyRgn, false);
                        }
                    }

                    bool invoked = TryInvokeSystemTrayIcon(BarItemKind.TrayOverflow, exactName);
                    if (!invoked) DockOverlayWindow.TriggerTrayOverflow();

                    if (!wasOpen) {
                        RepositionAndRevealOverflow(hiddenOverflow, centerX, anchorBottomY);
                    }
                } catch {
                } finally {
                    if (hiddenOverflow != IntPtr.Zero) {
                        NativeMethods.SetWindowRgn(hiddenOverflow, IntPtr.Zero, true);
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        private static void RepositionAndRevealOverflow(IntPtr overflowHwnd, int anchorCenterX, int anchorBottomY) {
            var pb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            if (overflowHwnd == IntPtr.Zero) {
                for (int i = 0; i < 60 && overflowHwnd == IntPtr.Zero; i++) {
                    IntPtr candidate = FindOverflowWindowHandle();
                    if (candidate != IntPtr.Zero && NativeMethods.IsWindowVisible(candidate)) {
                        overflowHwnd = candidate;
                        break;
                    }
                    Thread.Sleep(5);
                }
            } else {
                for (int i = 0; i < 400 && !NativeMethods.IsWindowVisible(overflowHwnd); i++) {
                    Thread.Sleep(1);
                }
            }

            if (overflowHwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(overflowHwnd)) return;

            // Move while still clipped to 0x0 so DWM never composites it at the bottom-right corner
            PositionOverflowOnce(overflowHwnd, anchorCenterX, anchorBottomY, pb);
            Thread.Sleep(15);
            PositionOverflowOnce(overflowHwnd, anchorCenterX, anchorBottomY, pb);

            // Unclip now that it is positioned cleanly above the ^ button
            NativeMethods.SetWindowRgn(overflowHwnd, IntPtr.Zero, true);

            for (int tick = 0; tick < 16; tick++) {
                if (!NativeMethods.IsWindowVisible(overflowHwnd)) break;
                PositionOverflowOnce(overflowHwnd, anchorCenterX, anchorBottomY, pb);
                Thread.Sleep(15);
            }
        }

        private static void PositionOverflowOnce(IntPtr hwnd, int anchorCenterX, int anchorBottomY, System.Drawing.Rectangle pb) {
            NativeMethods.RECT r;
            if (!NativeMethods.GetWindowRect(hwnd, out r)) return;
            if (r.Width < 30 || r.Height < 30) return;

            int targetX = anchorCenterX - r.Width / 2;
            if (targetX < pb.Left + 8) targetX = pb.Left + 8;
            if (targetX + r.Width > pb.Right - 8) targetX = pb.Right - 8 - r.Width;

            int targetY = anchorBottomY - r.Height;
            if (targetY < pb.Top + 8) targetY = pb.Top + 8;

            if (Math.Abs(r.Left - targetX) > 1 || Math.Abs(r.Top - targetY) > 1) {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, targetX, targetY, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }

        private void InvokePinnedApp(string exactName) {
            HideBar(immediate: true);
            Thread t = new Thread(() => {
                try {
                    IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero) NativeMethods.SetThreadDesktop(hDesk);
                    TryInvokeSystemTrayIcon(BarItemKind.PinnedApp, exactName);
                } catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        private void ReadForegroundInputLanguage() {
            try {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                uint pid;
                uint tid = (fg != IntPtr.Zero) ? NativeMethods.GetWindowThreadProcessId(fg, out pid) : 0;
                IntPtr hkl = NativeMethods.GetKeyboardLayout(tid);
                int langId = (int)((ulong)hkl.ToInt64() & 0xFFFF);
                if (langId > 0) {
                    CultureInfo ci = new CultureInfo(langId);
                    string code = ci.ThreeLetterISOLanguageName.ToUpperInvariant();
                    if (code.Length == 3) {
                        lock (_snapLock) {
                            _snapshot.InputCode = code;
                            _snapshot.InputName = ci.DisplayName;
                        }
                    }
                }
            } catch { }
        }

        private void ReadLocalAudioAndLang() {
            int pct; bool muted;
            if (SystemAudio.GetVolumeState(out pct, out muted)) {
                lock (_snapLock) {
                    _snapshot.VolumePercent = pct;
                    _snapshot.VolumeMuted = muted;
                }
            }
        }

        private void RefreshTraySnapshotAsync(bool rebuildLayout) {
            int seq = Interlocked.Increment(ref _refreshSeq);
            Thread t = new Thread(() => {
                try {
                    IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero) NativeMethods.SetThreadDesktop(hDesk);

                    TraySnapshot snap = QueryWindowsTraySnapshot();
                    if (snap == null) return;

                    lock (_snapLock) { _snapshot = snap; }
                    ReadLocalAudioAndLang();

                    if (!this.Dispatcher.HasShutdownStarted && seq == _refreshSeq) {
                        this.Dispatcher.BeginInvoke(new Action(() => {
                            if (rebuildLayout) BuildStripItems();
                            UpdateLiveVisuals();
                        }));
                    }
                } catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        private TraySnapshot QueryWindowsTraySnapshot() {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;

            AutomationElement elem = AutomationElement.FromHandle(tray);
            if (elem == null) return null;

            AutomationElementCollection all = elem.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            if (all == null) return null;

            TraySnapshot snap = new TraySnapshot();
            for (int i = 0; i < all.Count; i++) {
                try {
                    AutomationElement ch = all[i];
                    if (ch.Current.AutomationId != "SystemTrayIcon") continue;
                    string cls = ch.Current.ClassName ?? "";
                    string name = (ch.Current.Name ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (cls.IndexOf("ShowDesktopButton", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string lower = name.ToLowerInvariant();
                    if (lower.Contains("скрыт") || lower.Contains("hidden") || lower.Contains("overflow")) {
                        snap.OverflowName = name;
                    } else if (lower.Contains("индикатор ввода") || lower.Contains("input indicator")) {
                        snap.InputName = name;
                        snap.InputCode = ExtractLanguageCode(name);
                    } else if (lower.StartsWith("сеть") || lower.Contains("wi-fi") || lower.Contains("ethernet") ||
                               lower.Contains("доступ к интернету") || lower.Contains("internet access") || lower.Contains("network")) {
                        snap.NetworkName = name;
                        snap.NetworkConnected = !(lower.Contains("нет подключения") || lower.Contains("no internet") || lower.Contains("not connected") || lower.Contains("отключ"));
                        snap.NetworkWired = lower.Contains("ethernet");
                    } else if (lower.StartsWith("том") || lower.Contains("динамик") || lower.Contains("громк") ||
                               lower.Contains("volume") || lower.Contains("speaker") || lower.Contains("звук")) {
                        snap.VolumeName = name;
                    } else if (lower.StartsWith("мощност") || lower.Contains("батаре") || lower.Contains("аккумулятор") ||
                               lower.Contains("battery") || lower.Contains("power")) {
                        snap.BatteryName = name;
                    } else if (lower.StartsWith("часы") || lower.Contains("clock")) {
                        // handled live
                    } else {
                        snap.PinnedNames.Add(name);
                    }
                } catch { }
            }
            return snap;
        }

        private static bool TryInvokeSystemTrayIcon(BarItemKind kind, string exactName) {
            try {
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;
                AutomationElement elem = AutomationElement.FromHandle(tray);
                if (elem == null) return false;
                AutomationElementCollection all = elem.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                if (all == null) return false;

                AutomationElement target = null;
                for (int i = 0; i < all.Count; i++) {
                    try {
                        AutomationElement ch = all[i];
                        if (ch.Current.AutomationId != "SystemTrayIcon") continue;
                        string name = (ch.Current.Name ?? "").Trim();
                        if (string.IsNullOrEmpty(name)) continue;

                        if (!string.IsNullOrEmpty(exactName) && string.Equals(name, exactName, StringComparison.Ordinal)) {
                            target = ch;
                            break;
                        }
                        if (kind == BarItemKind.TrayOverflow) {
                            string lower = name.ToLowerInvariant();
                            if (lower.Contains("скрыт") || lower.Contains("hidden") || lower.Contains("overflow")) {
                                target = ch;
                                break;
                            }
                        }
                    } catch { }
                }

                if (target != null) {
                    object pat;
                    if (target.TryGetCurrentPattern(InvokePattern.Pattern, out pat)) {
                        ((InvokePattern)pat).Invoke();
                        return true;
                    }
                }
            } catch { }
            return false;
        }

        private static bool IsOverflowWindow(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return false;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            return cls == "TopLevelWindowForOverflowXamlIsland" || cls == "NotifyIconOverflowWindow";
        }

        private static IntPtr FindOverflowWindowHandle() {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((h, lp) => {
                if (IsOverflowWindow(h)) {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsAnyOverflowWindowVisible() {
            IntPtr h = FindOverflowWindowHandle();
            return h != IntPtr.Zero && NativeMethods.IsWindowVisible(h);
        }

        private static string ExtractLanguageCode(string name) {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("русск") || lower.Contains("russian")) return "RUS";
            if (lower.Contains("англ") || lower.Contains("english")) return "ENG";
            if (lower.Contains("укр") || lower.Contains("ukrain")) return "UKR";
            if (lower.Contains("нем") || lower.Contains("german")) return "DEU";
            if (lower.Contains("фран") || lower.Contains("french")) return "FRA";
            if (lower.Contains("исп") || lower.Contains("spanish")) return "ESP";
            return "LNG";
        }

        private static string FormatTooltip(string raw) {
            if (string.IsNullOrEmpty(raw)) return "";
            string clean = raw.Replace("\r\n", " — ").Replace("\n", " — ").Replace("\r", "");
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            return clean.Length > 96 ? clean.Substring(0, 93) + "..." : clean;
        }
    }
}
