using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskbarDock {
    internal enum FlyoutKind {
        None,
        Language,
        Network,
        Volume,
        Battery,
        Calendar
    }

    /// <summary>
    /// Crisp WPF DirectWrite minimalist dark matte flyout positioned directly above the clicked strip button.
    /// </summary>
    internal class DockFlyoutWindow : Window {
        private DockTrayBarWindow _parentBar;
        private Config _config;
        private FlyoutKind _kind = FlyoutKind.None;
        private int _anchorCenterX;
        private int _anchorBottomY;
        private IntPtr _targetAppHwnd;
        private Action _onStateChanged;

        private Border _rootBorder;
        private StackPanel _contentHost;
        private TranslateTransform _slideTransform;
        private DispatcherTimer _clockTimer;
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _isClosingAnim = false;

        // Cached Wi-Fi list so opening is instant without height jump
        private List<WifiNetworkInfo> _cachedWifi = new List<WifiNetworkInfo>();
        private DateTime _calMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

        // Volume live controls
        private TextBlock _volPctText;
        private Path _volIconPath;
        private Border _volFill;
        private Ellipse _volThumb;
        private Border _volTrackHost;
        private int _volPercent = 50;
        private bool _volMuted = false;
        private bool _draggingVol = false;

        // Calendar live controls
        private TextBlock _calTimeText;

        public FlyoutKind CurrentKind { get { return _kind; } }
        public IntPtr Hwnd { get { return _hwnd; } }

        public DockFlyoutWindow(DockTrayBarWindow parentBar, Config config, Action onStateChanged) {
            _parentBar = parentBar;
            _config = config;
            _onStateChanged = onStateChanged;

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

            _slideTransform = new TranslateTransform(0, 8);
            _contentHost = new StackPanel { Orientation = Orientation.Vertical };
            _rootBorder = new Border {
                Child = _contentHost,
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                RenderTransform = _slideTransform
            };
            this.Content = _rootBorder;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => {
                if (this.IsVisible && _kind == FlyoutKind.Calendar && _calTimeText != null) {
                    _calTimeText.Text = DateTime.Now.ToString("HH:mm:ss");
                }
            };

            this.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Escape) {
                    HideFlyout(animate: true);
                    e.Handled = true;
                }
            };
        }

        public void EnsureHandleCreated() {
            if (_hwnd != IntPtr.Zero) return;
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.EnsureHandle();
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
            HwndSource src = HwndSource.FromHwnd(_hwnd);
            if (src != null) src.AddHook(WndProcHook);
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == NativeMethods.WM_ACTIVATE) {
                int state = wParam.ToInt32() & 0xFFFF;
                if (state == 0 && !_isClosingAnim && this.IsVisible) {
                    IntPtr activating = lParam;
                    if (_parentBar != null && activating == _parentBar.Hwnd) {
                        return IntPtr.Zero;
                    }
                    HideFlyout(animate: false);
                    if (_parentBar != null) {
                        _parentBar.HideBar(immediate: true);
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void ShowFlyout(FlyoutKind kind, int anchorCenterX, int anchorBottomY, Config config, IntPtr targetAppHwnd, string activeLangCode) {
            EnsureHandleCreated();
            _kind = kind;
            _anchorCenterX = anchorCenterX;
            _anchorBottomY = anchorBottomY;
            _config = config ?? _config;
            _targetAppHwnd = targetAppHwnd;
            _isClosingAnim = false;
            _draggingVol = false;

            ApplyDockTheme();
            BuildContent(activeLangCode ?? "RUS");

            this.UpdateLayout();
            PositionAboveAnchor();

            if (!this.IsVisible) {
                this.Opacity = 0;
                _slideTransform.Y = 7;
                this.Show();
                PositionAboveAnchor();

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var slide = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(160)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(Window.OpacityProperty, fade);
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
            }

            NativeMethods.SetForegroundWindow(_hwnd);
            this.Activate();

            if (kind == FlyoutKind.Calendar) _clockTimer.Start();
            else _clockTimer.Stop();

            if (kind == FlyoutKind.Network) RefreshWifiAsync();
        }

        public void HideFlyout(bool animate) {
            _clockTimer.Stop();
            _draggingVol = false;
            if (!this.IsVisible) {
                _kind = FlyoutKind.None;
                return;
            }
            if (!animate) {
                _isClosingAnim = false;
                _kind = FlyoutKind.None;
                this.BeginAnimation(Window.OpacityProperty, null);
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                this.Hide();
                return;
            }
            if (_isClosingAnim) return;
            _isClosingAnim = true;
            var fade = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(100)) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slide = new DoubleAnimation(0, 5, TimeSpan.FromMilliseconds(100)) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, e) => {
                if (_isClosingAnim) {
                    _isClosingAnim = false;
                    _kind = FlyoutKind.None;
                    this.Hide();
                }
            };
            this.BeginAnimation(Window.OpacityProperty, fade);
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void ApplyDockTheme() {
            byte bgAlpha = (_config != null) ? (byte)Math.Max(180, Math.Min(255, _config.TaskbarOpacity)) : (byte)235;
            double radius = (_config != null) ? Math.Min(16.0, Math.Max(6.0, _config.CornerRadius)) : 14.0;
            _rootBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 24, 24, 26));
            _rootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
            _rootBorder.CornerRadius = new CornerRadius(radius);
        }

        private void PositionAboveAnchor() {
            this.UpdateLayout();
            double w = this.ActualWidth > 0 ? this.ActualWidth : this.DesiredSize.Width;
            double h = this.ActualHeight > 0 ? this.ActualHeight : this.DesiredSize.Height;
            var pb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

            double left = _anchorCenterX - w / 2.0;
            if (left < pb.Left + 8) left = pb.Left + 8;
            if (left + w > pb.Right - 8) left = pb.Right - 8 - w;

            double top = _anchorBottomY - h;
            if (top < pb.Top + 8) top = pb.Top + 8;

            this.Left = Math.Round(left);
            this.Top = Math.Round(top);
        }

        private void BuildContent(string activeLangCode) {
            _contentHost.Children.Clear();
            _volPctText = null;
            _volIconPath = null;
            _volFill = null;
            _volThumb = null;
            _volTrackHost = null;
            _calTimeText = null;

            switch (_kind) {
                case FlyoutKind.Volume:
                    BuildVolumeUi();
                    break;
                case FlyoutKind.Battery:
                    BuildBatteryUi();
                    break;
                case FlyoutKind.Language:
                    BuildLanguageUi(activeLangCode);
                    break;
                case FlyoutKind.Network:
                    BuildNetworkUi();
                    break;
                case FlyoutKind.Calendar:
                    BuildCalendarUi();
                    break;
            }
        }

        private void BuildVolumeUi() {
            _contentHost.Width = 228;
            SystemAudio.GetVolumeState(out _volPercent, out _volMuted);

            var hdr = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
            hdr.Children.Add(new TextBlock {
                Text = "Громкость",
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            });
            _volPctText = new TextBlock {
                Text = _volMuted ? "Без звука" : (_volPercent + "%"),
                Foreground = new SolidColorBrush(Color.FromRgb(168, 175, 190)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_volPctText, Dock.Right);
            hdr.Children.Add(_volPctText);
            _contentHost.Children.Add(hdr);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Height = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _volIconPath = new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform
            };
            var muteBtn = CreateHoverTile(_volIconPath, 28, 28, () => {
                _volMuted = !_volMuted;
                SystemAudio.SetMute(_volMuted);
                UpdateVolumeVisuals();
                if (_onStateChanged != null) _onStateChanged();
            });
            Grid.SetColumn(muteBtn, 0);
            row.Children.Add(muteBtn);

            _volTrackHost = new Border {
                Background = Brushes.Transparent,
                Margin = new Thickness(8, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            var trackGrid = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 20 };
            var bgTrack = new Border {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _volFill = new Border {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            _volThumb = new Ellipse {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            trackGrid.Children.Add(bgTrack);
            trackGrid.Children.Add(_volFill);
            trackGrid.Children.Add(_volThumb);
            _volTrackHost.Child = trackGrid;

            _volTrackHost.MouseLeftButtonDown += (s, e) => {
                _draggingVol = true;
                _volTrackHost.CaptureMouse();
                ApplyVolumeAtPoint(e.GetPosition(_volTrackHost).X);
            };
            _volTrackHost.MouseMove += (s, e) => {
                if (_draggingVol) ApplyVolumeAtPoint(e.GetPosition(_volTrackHost).X);
            };
            _volTrackHost.MouseLeftButtonUp += (s, e) => {
                if (_draggingVol) {
                    _draggingVol = false;
                    _volTrackHost.ReleaseMouseCapture();
                    if (_onStateChanged != null) _onStateChanged();
                }
            };
            _volTrackHost.MouseWheel += (s, e) => {
                int next = Math.Max(0, Math.Min(100, _volPercent + (e.Delta > 0 ? 4 : -4)));
                _volPercent = next;
                _volMuted = (next == 0);
                SystemAudio.SetVolumePercent(next);
                UpdateVolumeVisuals();
                if (_onStateChanged != null) _onStateChanged();
            };

            Grid.SetColumn(_volTrackHost, 1);
            row.Children.Add(_volTrackHost);
            _contentHost.Children.Add(row);

            _volTrackHost.Loaded += (s, e) => UpdateVolumeVisuals();
            UpdateVolumeVisuals();

            AddFooterButton("Все устройства и параметры", () => {
                HideBoth();
                DockOverlayWindow.TriggerQuickSettings();
            });
        }

        private void ApplyVolumeAtPoint(double x) {
            double w = _volTrackHost.ActualWidth;
            if (w <= 1) w = 180;
            double ratio = Math.Max(0.0, Math.Min(1.0, x / w));
            _volPercent = (int)Math.Round(ratio * 100.0);
            _volMuted = (_volPercent == 0);
            SystemAudio.SetVolumePercent(_volPercent);
            UpdateVolumeVisuals();
            if (_onStateChanged != null) _onStateChanged();
        }

        private void UpdateVolumeVisuals() {
            if (_volPctText != null) {
                _volPctText.Text = _volMuted ? "Без звука" : (_volPercent + "%");
            }
            if (_volIconPath != null) {
                _volIconPath.Data = BuildSpeakerGeometry(_volMuted || _volPercent == 0, _volPercent);
            }
            if (_volFill != null && _volThumb != null && _volTrackHost != null) {
                double w = _volTrackHost.ActualWidth > 0 ? _volTrackHost.ActualWidth : 186;
                double ratio = _volMuted ? 0.0 : (_volPercent / 100.0);
                double fillW = Math.Max(4, w * ratio);
                _volFill.Width = fillW;
                _volFill.Background = new SolidColorBrush(_volMuted ? Color.FromRgb(130, 136, 150) : Color.FromRgb(76, 148, 246));
                double thumbX = Math.Max(0, Math.Min(w - 12, w * ratio - 6));
                _volThumb.Margin = new Thickness(thumbX, 0, 0, 0);
            }
        }

        private void BuildBatteryUi() {
            _contentHost.Width = 220;
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            int pct = Math.Max(0, Math.Min(100, (int)Math.Round(ps.BatteryLifePercent * 100)));
            bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            var hdr = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
            hdr.Children.Add(new TextBlock {
                Text = "Батарея",
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            });
            var valTb = new TextBlock {
                Text = pct + "%",
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(valTb, Dock.Right);
            hdr.Children.Add(valTb);
            _contentHost.Children.Add(hdr);

            var barBg = new Border {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(44, 255, 255, 255)),
                Margin = new Thickness(2, 0, 2, 8)
            };
            Color fillCol = charging ? Color.FromRgb(95, 215, 130) : (pct <= 20 ? Color.FromRgb(255, 105, 97) : Color.FromRgb(76, 148, 246));
            barBg.Child = new Border {
                Height = 6,
                Width = Math.Max(6, 216 * (pct / 100.0)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(fillCol)
            };
            _contentHost.Children.Add(barBg);

            string status = charging ? "Подключено к сети" : "Питание от батареи";
            if (!charging && ps.BatteryLifeRemaining > 0) {
                TimeSpan ts = TimeSpan.FromSeconds(ps.BatteryLifeRemaining);
                status += string.Format(" (~{0} ч {1:00} м)", (int)ts.TotalHours, ts.Minutes);
            }
            _contentHost.Children.Add(new TextBlock {
                Text = status,
                Foreground = new SolidColorBrush(Color.FromRgb(168, 175, 190)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Margin = new Thickness(2, 0, 2, 10)
            });

            AddFooterButton("Параметры питания", () => {
                HideBoth();
                try { Process.Start("ms-settings:powersleep"); } catch { DockOverlayWindow.TriggerQuickSettings(); }
            });
        }

        private void BuildLanguageUi(string activeCode) {
            _contentHost.Width = 190;
            _contentHost.Children.Add(new TextBlock {
                Text = "Раскладка",
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 4, 8)
            });

            var list = SystemKeyboard.GetInstalledLayouts();
            foreach (var lang in list) {
                var captured = lang;
                bool active = string.Equals(lang.Code, activeCode, StringComparison.OrdinalIgnoreCase);

                var row = new DockPanel { LastChildFill = true };
                var codeTb = new TextBlock {
                    Text = lang.Code,
                    Width = 36,
                    Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(codeTb, Dock.Left);
                row.Children.Add(codeTb);

                if (active) {
                    var dot = new Ellipse {
                        Width = 6,
                        Height = 6,
                        Fill = new SolidColorBrush(Color.FromRgb(76, 148, 246)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    DockPanel.SetDock(dot, Dock.Right);
                    row.Children.Add(dot);
                }

                row.Children.Add(new TextBlock {
                    Text = lang.DisplayName,
                    Foreground = new SolidColorBrush(Color.FromRgb(176, 182, 196)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var btn = CreateHoverRow(row, active, () => {
                    if (_targetAppHwnd != IntPtr.Zero) {
                        NativeMethods.PostMessage(_targetAppHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, captured.Hkl);
                    }
                    if (_parentBar != null) _parentBar.ApplyLanguageLocally(captured.Code, captured.DisplayName);
                    HideFlyout(animate: true);
                });
                _contentHost.Children.Add(btn);
            }
        }

        private void BuildNetworkUi() {
            _contentHost.Width = 248;
            _contentHost.Children.Add(new TextBlock {
                Text = "Wi-Fi",
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 250)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 4, 8)
            });

            if (_cachedWifi.Count == 0) {
                _contentHost.Children.Add(new TextBlock {
                    Text = "Поиск сетей...",
                    Foreground = new SolidColorBrush(Color.FromRgb(165, 172, 186)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11.5,
                    Margin = new Thickness(4, 4, 4, 10)
                });
            } else {
                int max = Math.Min(6, _cachedWifi.Count);
                for (int i = 0; i < max; i++) {
                    var net = _cachedWifi[i];
                    var row = new DockPanel { LastChildFill = true };

                    var ico = new Path {
                        Stroke = new SolidColorBrush(net.IsConnected ? Color.FromRgb(115, 220, 145) : Color.FromRgb(220, 226, 238)),
                        StrokeThickness = 1.4,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Width = 15,
                        Height = 13,
                        Stretch = Stretch.Uniform,
                        Data = BuildWifiGeometry(net.SignalQuality, true),
                        Margin = new Thickness(0, 0, 9, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(ico, Dock.Left);
                    row.Children.Add(ico);

                    var statusTb = new TextBlock {
                        Text = net.IsConnected ? "Подключено" : (net.HasProfile ? (net.SignalQuality + "%") : "Новая"),
                        Foreground = new SolidColorBrush(net.IsConnected ? Color.FromRgb(115, 220, 145) : Color.FromRgb(152, 160, 175)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    DockPanel.SetDock(statusTb, Dock.Right);
                    row.Children.Add(statusTb);

                    row.Children.Add(new TextBlock {
                        Text = net.Ssid,
                        Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 248)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    var captured = net;
                    _contentHost.Children.Add(CreateHoverRow(row, net.IsConnected, () => {
                        if (captured.IsConnected) {
                            ThreadPool.QueueUserWorkItem(_ => {
                                SystemWifi.Disconnect(captured.InterfaceGuid);
                                Thread.Sleep(450);
                                RefreshWifiAsync();
                            });
                        } else if (captured.HasProfile) {
                            ThreadPool.QueueUserWorkItem(_ => {
                                SystemWifi.ConnectProfile(captured.InterfaceGuid, captured.ProfileName);
                                Thread.Sleep(900);
                                RefreshWifiAsync();
                            });
                        } else {
                            HideBoth();
                            try { Process.Start("ms-settings:network-wifi"); } catch { }
                        }
                    }));
                }
            }

            AddFooterButton("Параметры Wi-Fi", () => {
                HideBoth();
                try { Process.Start("ms-settings:network-wifi"); } catch { DockOverlayWindow.TriggerQuickSettings(); }
            });
        }

        private void RefreshWifiAsync() {
            Thread t = new Thread(() => {
                try {
                    string adapter;
                    var nets = SystemWifi.GetNetworks(out adapter);
                    if (!this.Dispatcher.HasShutdownStarted) {
                        this.Dispatcher.BeginInvoke(new Action(() => {
                            if (_kind != FlyoutKind.Network || !this.IsVisible) return;
                            _cachedWifi = nets;
                            BuildContent("");
                            PositionAboveAnchor();
                        }));
                    }
                } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void BuildCalendarUi() {
            _contentHost.Width = 236;
            DateTime now = DateTime.Now;

            _calTimeText = new TextBlock {
                Text = now.ToString("HH:mm:ss"),
                Foreground = new SolidColorBrush(Color.FromRgb(244, 246, 252)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 4, 0)
            };
            _contentHost.Children.Add(_calTimeText);

            string dateLabel = now.ToString("dddd, d MMMM", CultureInfo.GetCultureInfo("ru-RU"));
            if (!string.IsNullOrEmpty(dateLabel)) dateLabel = char.ToUpper(dateLabel[0]) + dateLabel.Substring(1);
            _contentHost.Children.Add(new TextBlock {
                Text = dateLabel,
                Foreground = new SolidColorBrush(Color.FromRgb(168, 175, 190)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Margin = new Thickness(4, 1, 4, 10)
            });

            // Month navigation
            var navPanel = new DockPanel { Margin = new Thickness(4, 0, 2, 6) };
            string mName = _calMonth.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
            if (!string.IsNullOrEmpty(mName)) mName = char.ToUpper(mName[0]) + mName.Substring(1);
            navPanel.Children.Add(new TextBlock {
                Text = mName,
                Foreground = new SolidColorBrush(Color.FromRgb(236, 238, 246)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            arrows.Children.Add(CreateHoverTile(CreateChevronPath(-1), 24, 24, () => {
                _calMonth = _calMonth.AddMonths(-1);
                BuildCalendarUiInPlace();
            }));
            arrows.Children.Add(CreateHoverTile(CreateChevronPath(1), 24, 24, () => {
                _calMonth = _calMonth.AddMonths(1);
                BuildCalendarUiInPlace();
            }));
            DockPanel.SetDock(arrows, Dock.Right);
            navPanel.Children.Add(arrows);
            _contentHost.Children.Add(navPanel);

            // Calendar grid
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

            string[] dows = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
            for (int c = 0; c < 7; c++) {
                var tb = new TextBlock {
                    Text = dows[c],
                    Foreground = new SolidColorBrush(Color.FromRgb(145, 152, 168)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(tb, 0);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }

            int offset = ((int)_calMonth.DayOfWeek + 6) % 7;
            DateTime gridStart = _calMonth.AddDays(-offset);
            for (int row = 0; row < 6; row++) {
                for (int col = 0; col < 7; col++) {
                    DateTime d = gridStart.AddDays(row * 7 + col);
                    bool isCurMonth = (d.Month == _calMonth.Month);
                    bool isToday = (d.Date == now.Date);

                    var cell = new Border {
                        CornerRadius = new CornerRadius(6),
                        Background = isToday ? new SolidColorBrush(Color.FromRgb(76, 148, 246)) : Brushes.Transparent,
                        Margin = new Thickness(2)
                    };
                    cell.Child = new TextBlock {
                        Text = d.Day.ToString(),
                        Foreground = new SolidColorBrush(isToday ? Colors.White : (isCurMonth ? Color.FromRgb(234, 236, 244) : Color.FromRgb(95, 100, 114))),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11.5,
                        FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(cell, row + 1);
                    Grid.SetColumn(cell, col);
                    grid.Children.Add(cell);
                }
            }
            _contentHost.Children.Add(grid);

            AddFooterButton("Уведомления и события", () => {
                HideBoth();
                DockOverlayWindow.TriggerNotificationCenter();
            });
        }

        private void BuildCalendarUiInPlace() {
            _contentHost.Children.Clear();
            BuildCalendarUi();
        }

        private Border CreateHoverRow(UIElement content, bool highlighted, Action onClick) {
            var baseBrush = highlighted ? new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)) : Brushes.Transparent;
            var hoverBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            var b = new Border {
                Child = content,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(8),
                Background = baseBrush,
                Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Background = hoverBrush;
            b.MouseLeave += (s, e) => b.Background = baseBrush;
            b.MouseLeftButtonUp += (s, e) => { if (onClick != null) onClick(); };
            return b;
        }

        private Border CreateHoverTile(UIElement content, double w, double h, Action onClick) {
            var b = new Border {
                Child = content,
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { if (onClick != null) onClick(); };
            return b;
        }

        private void AddFooterButton(string text, Action onClick) {
            _contentHost.Children.Add(new Border {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                Margin = new Thickness(2, 4, 2, 4)
            });
            var tb = new TextBlock {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 184, 198)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var btn = new Border {
                Child = tb,
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
            btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
            btn.MouseLeftButtonUp += (s, e) => { if (onClick != null) onClick(); };
            _contentHost.Children.Add(btn);
        }

        private void HideBoth() {
            HideFlyout(animate: false);
            if (_parentBar != null) _parentBar.HideBar(immediate: true);
        }

        private static Path CreateChevronPath(int dir) {
            string d = (dir < 0) ? "M 9,3 L 5,7 L 9,11" : "M 5,3 L 9,7 L 5,11";
            return new Path {
                Stroke = new SolidColorBrush(Color.FromRgb(220, 226, 238)),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 14,
                Height = 14,
                Data = Geometry.Parse(d)
            };
        }

        internal static Geometry BuildSpeakerGeometry(bool muted, int pct) {
            string g = "M 1,5.5 L 4,5.5 L 7.5,2.5 L 7.5,13.5 L 4,10.5 L 1,10.5 Z ";
            if (muted) {
                g += "M 10,5.5 L 14.5,10.5 M 14.5,5.5 L 10,10.5";
            } else {
                g += "M 10.2,5.3 A 4.2,4.2 0 0 1 10.2,10.7";
                if (pct >= 40) {
                    g += " M 12.4,3.4 A 6.8,6.8 0 0 1 12.4,12.6";
                }
            }
            return Geometry.Parse(g);
        }

        internal static Geometry BuildWifiGeometry(int sig, bool connected) {
            string g = "M 7.2,11.6 A 0.7,0.7 0 1 1 8.8,11.6 A 0.7,0.7 0 1 1 7.2,11.6";
            if (!connected) {
                g = "M 4.5,8.6 A 4.8,4.8 0 0 1 11.5,8.6 M 2.0,6.1 A 8.4,8.4 0 0 1 14.0,6.1 M 3,12 L 13,3";
                return Geometry.Parse(g);
            }
            if (sig >= 20) g += " M 5.1,9.3 A 4.1,4.1 0 0 1 10.9,9.3";
            if (sig >= 50) g += " M 3.0,7.0 A 7.1,7.1 0 0 1 13.0,7.0";
            if (sig >= 75) g += " M 1.0,4.8 A 10.0,10.0 0 0 1 15.0,4.8";
            return Geometry.Parse(g);
        }
    }
}
