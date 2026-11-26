using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarDock {
    public class SettingsForm : Form {
        private Config _config;
        private DockEngine _engine;
        private Action _onConfigChanged;
        private bool _isLoadingUI = false;

        private RadioButton _rbOnlyDock;
        private RadioButton _rbSplitDock;

        private TrackBar _tbRadius;
        private Label _lblRadiusVal;

        private TrackBar _tbPadding;
        private Label _lblPaddingVal;

        private TrackBar _tbMargin;
        private Label _lblMarginVal;

        private TrackBar _tbOpacity;
        private Label _lblOpacityVal;

        private CheckBox _cbFloatOver;
        private CheckBox _cbAutoHideFullscreen;
        private CheckBox _cbShowQuickSettingsBtn;

        private CheckBox _cbEnabled;
        private CheckBox _cbAutoStart;

        public SettingsForm(Config config, DockEngine engine, Action onConfigChanged) {
            _config = config;
            _engine = engine;
            _onConfigChanged = onConfigChanged;

            InitializeComponent();
            LoadConfigToUI();
        }

        private void InitializeComponent() {
            this.Text = "TaskbarDock — Настройки плавающего дока";
            this.Size = new Size(540, 710);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(28, 28, 32);
            this.ForeColor = Color.FromArgb(240, 240, 245);
            this.Font = new Font("Segoe UI", 9.2f, FontStyle.Regular);

            try {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(icoPath)) {
                    this.Icon = new Icon(icoPath);
                }
            } catch { }

            int y = 14;

            // Header Banner
            Panel pnlHeader = new Panel {
                Location = new Point(16, y),
                Size = new Size(492, 58),
                BackColor = Color.FromArgb(38, 38, 44)
            };
            Label lblTitle = new Label {
                Text = "TaskbarDock — Жидкое стекло",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 175, 255),
                Location = new Point(14, 5),
                AutoSize = true
            };
            Label lblSub = new Label {
                Text = "Плавающий док с яркими иконками, прозрачным стеклом и кнопкой настроек",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(175, 180, 195),
                Location = new Point(15, 32),
                AutoSize = true
            };
            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSub);
            this.Controls.Add(pnlHeader);
            y += 68;

            // Mode Group
            GroupBox gbMode = CreateGroupBox("Режим отображения", 16, y, 492, 85);
            _rbOnlyDock = new RadioButton {
                Text = "Только док (скрыть пустые края панели, только приложения)",
                Location = new Point(18, 22),
                Size = new Size(460, 24),
                Checked = true
            };
            _rbOnlyDock.CheckedChanged += (s, e) => { if (_rbOnlyDock.Checked) ApplyChangesLive(); };

            _rbSplitDock = new RadioButton {
                Text = "Раздельный док (док с приложениями + островок с часами)",
                Location = new Point(18, 50),
                Size = new Size(460, 24)
            };
            _rbSplitDock.CheckedChanged += (s, e) => { if (_rbSplitDock.Checked) ApplyChangesLive(); };

            gbMode.Controls.Add(_rbOnlyDock);
            gbMode.Controls.Add(_rbSplitDock);
            this.Controls.Add(gbMode);
            y += 94;

            // Style Group
            GroupBox gbStyle = CreateGroupBox("Внешний вид и «Жидкое стекло»", 16, y, 492, 190);
            int sy = 22;

            // Opacity (Liquid Glass)
            Label lblOpacity = new Label { Text = "Прозрачность стекла:", Location = new Point(18, sy), AutoSize = true };
            _lblOpacityVal = new Label { Text = "90%", Location = new Point(430, sy), AutoSize = true, ForeColor = Color.FromArgb(100, 175, 255) };
            _tbOpacity = new TrackBar { Location = new Point(180, sy - 4), Size = new Size(240, 40), Minimum = 120, Maximum = 255, TickFrequency = 15 };
            _tbOpacity.ValueChanged += (s, e) => {
                int pct = (int)Math.Round((_tbOpacity.Value / 255.0) * 100);
                _lblOpacityVal.Text = pct + "%";
                ApplyChangesLive();
            };
            gbStyle.Controls.Add(lblOpacity);
            gbStyle.Controls.Add(_lblOpacityVal);
            gbStyle.Controls.Add(_tbOpacity);
            sy += 42;

            // Corner Radius
            Label lblRadius = new Label { Text = "Скругление углов (гладкое):", Location = new Point(18, sy), AutoSize = true };
            _lblRadiusVal = new Label { Text = "16 px", Location = new Point(430, sy), AutoSize = true, ForeColor = Color.FromArgb(100, 175, 255) };
            _tbRadius = new TrackBar { Location = new Point(180, sy - 4), Size = new Size(240, 40), Minimum = 0, Maximum = 26, TickFrequency = 2 };
            _tbRadius.ValueChanged += (s, e) => {
                _lblRadiusVal.Text = _tbRadius.Value + " px";
                ApplyChangesLive();
            };
            gbStyle.Controls.Add(lblRadius);
            gbStyle.Controls.Add(_lblRadiusVal);
            gbStyle.Controls.Add(_tbRadius);
            sy += 42;

            // Padding
            Label lblPadding = new Label { Text = "Отступ по бокам:", Location = new Point(18, sy), AutoSize = true };
            _lblPaddingVal = new Label { Text = "12 px", Location = new Point(430, sy), AutoSize = true, ForeColor = Color.FromArgb(100, 175, 255) };
            _tbPadding = new TrackBar { Location = new Point(180, sy - 4), Size = new Size(240, 40), Minimum = 0, Maximum = 30, TickFrequency = 5 };
            _tbPadding.ValueChanged += (s, e) => {
                _lblPaddingVal.Text = _tbPadding.Value + " px";
                ApplyChangesLive();
            };
            gbStyle.Controls.Add(lblPadding);
            gbStyle.Controls.Add(_lblPaddingVal);
            gbStyle.Controls.Add(_tbPadding);
            sy += 42;

            // Margin from bottom of screen
            Label lblMargin = new Label { Text = "Отступ от низа экрана:", Location = new Point(18, sy), AutoSize = true };
            _lblMarginVal = new Label { Text = "8 px", Location = new Point(430, sy), AutoSize = true, ForeColor = Color.FromArgb(100, 175, 255) };
            _tbMargin = new TrackBar { Location = new Point(180, sy - 4), Size = new Size(240, 40), Minimum = 0, Maximum = 16, TickFrequency = 2 };
            _tbMargin.ValueChanged += (s, e) => {
                _lblMarginVal.Text = _tbMargin.Value + " px";
                ApplyChangesLive();
            };
            gbStyle.Controls.Add(lblMargin);
            gbStyle.Controls.Add(_lblMarginVal);
            gbStyle.Controls.Add(_tbMargin);

            this.Controls.Add(gbStyle);
            y += 200;

            // Behavior Group
            GroupBox gbBehavior = CreateGroupBox("Поведение и функции", 16, y, 492, 115);
            _cbFloatOver = new CheckBox {
                Text = "Док поверх окон (развернутые окна заполняют весь экран под доком)",
                Location = new Point(18, 24),
                AutoSize = true
            };
            _cbFloatOver.CheckedChanged += (s, e) => ApplyChangesLive();

            _cbAutoHideFullscreen = new CheckBox {
                Text = "Автоматически скрывать док в полноэкранных приложениях (игры, видео F11)",
                Location = new Point(18, 52),
                AutoSize = true
            };
            _cbAutoHideFullscreen.CheckedChanged += (s, e) => ApplyChangesLive();

            _cbShowQuickSettingsBtn = new CheckBox {
                Text = "Кнопка быстрых настроек справа в доке (меню настроек, звука, Wi-Fi)",
                Location = new Point(18, 80),
                AutoSize = true
            };
            _cbShowQuickSettingsBtn.CheckedChanged += (s, e) => ApplyChangesLive();

            gbBehavior.Controls.Add(_cbFloatOver);
            gbBehavior.Controls.Add(_cbAutoHideFullscreen);
            gbBehavior.Controls.Add(_cbShowQuickSettingsBtn);
            this.Controls.Add(gbBehavior);
            y += 125;

            // Options Group
            GroupBox gbOpts = CreateGroupBox("Система и горячие клавиши", 16, y, 492, 80);
            _cbEnabled = new CheckBox {
                Text = "Включить док",
                Location = new Point(18, 22),
                AutoSize = true
            };
            _cbEnabled.CheckedChanged += (s, e) => ApplyChangesLive();

            _cbAutoStart = new CheckBox {
                Text = "Запускать вместе с Windows (автозагрузка)",
                Location = new Point(18, 48),
                AutoSize = true
            };
            _cbAutoStart.CheckedChanged += (s, e) => {
                if (_isLoadingUI) return;
                _config.AutoStart = _cbAutoStart.Checked;
                Config.SetAutoStart(_cbAutoStart.Checked);
            };

            Label lblHotkey = new Label {
                Text = "Горячая клавиша: Win + Alt + D",
                ForeColor = Color.FromArgb(160, 205, 255),
                Location = new Point(255, 22),
                AutoSize = true
            };

            gbOpts.Controls.Add(_cbEnabled);
            gbOpts.Controls.Add(_cbAutoStart);
            gbOpts.Controls.Add(lblHotkey);
            this.Controls.Add(gbOpts);
            y += 90;

            // Action Buttons
            Button btnReset = new Button {
                Text = "Сбросить по умолчанию",
                Size = new Size(160, 32),
                Location = new Point(16, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(48, 48, 54),
                ForeColor = Color.FromArgb(220, 220, 225)
            };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += (s, e) => ResetToDefaults();

            Button btnClose = new Button {
                Text = "Закрыть в трей",
                Size = new Size(140, 32),
                Location = new Point(368, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 120, 230),
                ForeColor = Color.White
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Hide();

            this.Controls.Add(btnReset);
            this.Controls.Add(btnClose);

            this.FormClosing += (s, e) => {
                if (e.CloseReason == CloseReason.UserClosing) {
                    e.Cancel = true;
                    this.Hide();
                }
            };
        }

        private GroupBox CreateGroupBox(string text, int x, int y, int w, int h) {
            GroupBox gb = new GroupBox {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                ForeColor = Color.FromArgb(210, 215, 225),
                BackColor = Color.FromArgb(34, 34, 40)
            };
            return gb;
        }

        private void LoadConfigToUI() {
            _isLoadingUI = true;
            try {
                if (_config.Mode == 1) {
                    _rbSplitDock.Checked = true;
                } else {
                    _rbOnlyDock.Checked = true;
                }

                _tbOpacity.Value = Math.Max(_tbOpacity.Minimum, Math.Min(_tbOpacity.Maximum, _config.TaskbarOpacity));
                int pct = (int)Math.Round((_tbOpacity.Value / 255.0) * 100);
                _lblOpacityVal.Text = pct + "%";

                _tbRadius.Value = Math.Max(_tbRadius.Minimum, Math.Min(_tbRadius.Maximum, _config.CornerRadius));
                _lblRadiusVal.Text = _tbRadius.Value + " px";

                _tbPadding.Value = Math.Max(_tbPadding.Minimum, Math.Min(_tbPadding.Maximum, _config.PaddingHorizontal));
                _lblPaddingVal.Text = _tbPadding.Value + " px";

                _tbMargin.Value = Math.Max(_tbMargin.Minimum, Math.Min(_tbMargin.Maximum, _config.MarginBottom));
                _lblMarginVal.Text = _tbMargin.Value + " px";

                _cbFloatOver.Checked = _config.FloatOverWindows;
                _cbAutoHideFullscreen.Checked = _config.AutoHideInFullscreen;
                _cbShowQuickSettingsBtn.Checked = _config.ShowQuickSettingsButton;

                _cbEnabled.Checked = _config.Enabled;
                _cbAutoStart.Checked = _config.AutoStart;
            } finally {
                _isLoadingUI = false;
            }
        }

        private void ApplyChangesLive() {
            if (_isLoadingUI) return;

            _config.Mode = _rbSplitDock.Checked ? 1 : 0;
            _config.TaskbarOpacity = _tbOpacity.Value;
            _config.CornerRadius = _tbRadius.Value;
            _config.PaddingHorizontal = _tbPadding.Value;
            _config.MarginBottom = _tbMargin.Value;
            _config.FloatOverWindows = _cbFloatOver.Checked;
            _config.AutoHideInFullscreen = _cbAutoHideFullscreen.Checked;
            _config.ShowQuickSettingsButton = _cbShowQuickSettingsBtn.Checked;
            _config.Enabled = _cbEnabled.Checked;
            _config.AutoStart = _cbAutoStart.Checked;

            _config.Save();
            if (_onConfigChanged != null) _onConfigChanged();
        }

        private void ResetToDefaults() {
            _config.Mode = 0;
            _config.TaskbarOpacity = 235;
            _config.CornerRadius = 16;
            _config.PaddingHorizontal = 16;
            _config.MarginBottom = 4;
            _config.MarginTop = 4;
            _config.FloatOverWindows = false;
            _config.AutoHideInFullscreen = true;
            _config.ShowQuickSettingsButton = true;
            _config.Enabled = true;

            LoadConfigToUI();
            ApplyChangesLive();
        }
    }
}
