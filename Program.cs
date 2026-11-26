using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarDock {
    internal static class Program {
        public static uint MsgShowSettings = 0;

        [STAThread]
        static void Main(string[] args) {
            Process current = Process.GetCurrentProcess();
            Process[] existing = Process.GetProcessesByName(current.ProcessName);
            if (existing.Length > 1) {
                MsgShowSettings = NativeMethods.RegisterWindowMessage("TaskbarDock_ShowSettings_Msg");
                Logger.Log("Another instance found by ProcessName, broadcasting MsgShowSettings.");
                NativeMethods.PostMessage((IntPtr)NativeMethods.HWND_BROADCAST, MsgShowSettings, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            try {
                IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero) {
                    NativeMethods.SetThreadDesktop(hDesk);
                }
            } catch { }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Logger.Log("ThreadException: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Logger.Log("UnhandledException: " + e.ExceptionObject);

            MsgShowSettings = NativeMethods.RegisterWindowMessage("TaskbarDock_ShowSettings_Msg");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            DockAppContext context = new DockAppContext();
            Application.Run(context);
        }
    }

    internal class DockAppContext : ApplicationContext {
        private Config _config;
        private DockEngine _engine;
        private NotifyIcon _notifyIcon;
        private SettingsForm _settingsForm;
        private MessageWindow _msgWindow;

        private MenuItem _miEnabled;
        private MenuItem _miOnlyDock;
        private MenuItem _miSplitDock;
        private MenuItem _miAutoStart;

        public DockAppContext() {
            Logger.Log("DockAppContext initializing...");
            _config = Config.Load();
            Logger.Log("Config loaded: Enabled=" + _config.Enabled);
            _engine = new DockEngine(ShowSettings);
            Logger.Log("DockEngine created.");

            InitNotifyIcon();
            Logger.Log("NotifyIcon initialized.");
            InitMessageWindow();
            Logger.Log("MessageWindow initialized.");

            _settingsForm = new SettingsForm(_config, _engine, OnConfigChanged);
            Logger.Log("SettingsForm created.");
            _engine.Start(_config);
            Logger.Log("DockEngine started.");

            Application.ApplicationExit += (s, e) => Cleanup();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();
        }

        private void InitNotifyIcon() {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "TaskbarDock — Панель задач как док";

            try {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(icoPath)) {
                    _notifyIcon.Icon = new Icon(icoPath);
                } else {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            } catch {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            ContextMenu menu = new ContextMenu();

            _miEnabled = new MenuItem("Включить док", (s, e) => {
                _config.Enabled = !_config.Enabled;
                _config.Save();
                UpdateMenuState();
                _engine.TriggerUpdate();
            });

            MenuItem miSettings = new MenuItem("Настройки...", (s, e) => ShowSettings());

            _miOnlyDock = new MenuItem("Режим: Только док", (s, e) => {
                _config.Mode = 0;
                _config.Save();
                UpdateMenuState();
                _engine.TriggerUpdate();
            });

            _miSplitDock = new MenuItem("Режим: Раздельный док", (s, e) => {
                _config.Mode = 1;
                _config.Save();
                UpdateMenuState();
                _engine.TriggerUpdate();
            });

            MenuItem miQuickSettings = new MenuItem("Быстрые настройки (Win+A)", (s, e) => {
                DockOverlayWindow.TriggerQuickSettings();
            });

            _miAutoStart = new MenuItem("Автозапуск с Windows", (s, e) => {
                _config.AutoStart = !_config.AutoStart;
                _config.Save();
                UpdateMenuState();
            });

            MenuItem miRestore = new MenuItem("Восстановить панель задач", (s, e) => {
                _config.Enabled = false;
                _config.Save();
                UpdateMenuState();
                _engine.TriggerUpdate();
            });

            MenuItem miExit = new MenuItem("Выход", (s, e) => {
                Cleanup();
                Application.Exit();
            });

            menu.MenuItems.Add(_miEnabled);
            menu.MenuItems.Add(miSettings);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(_miOnlyDock);
            menu.MenuItems.Add(_miSplitDock);
            menu.MenuItems.Add(miQuickSettings);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(_miAutoStart);
            menu.MenuItems.Add(miRestore);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(miExit);

            _notifyIcon.ContextMenu = menu;
            _notifyIcon.DoubleClick += (s, e) => ShowSettings();
            _notifyIcon.Visible = true;

            UpdateMenuState();
        }

        private void InitMessageWindow() {
            _msgWindow = new MessageWindow(this);
            NativeMethods.RegisterHotKey(_msgWindow.Handle, 1, NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)Keys.D);
            NativeMethods.RegisterHotKey(_msgWindow.Handle, 2, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)Keys.D);
        }

        private void UpdateMenuState() {
            if (_miEnabled != null) _miEnabled.Checked = _config.Enabled;
            if (_miOnlyDock != null) _miOnlyDock.Checked = (_config.Mode == 0);
            if (_miSplitDock != null) _miSplitDock.Checked = (_config.Mode == 1);
            if (_miAutoStart != null) _miAutoStart.Checked = _config.AutoStart;
        }

        public void ShowSettings() {
            Logger.Log("ShowSettings requested.");
            if (_msgWindow != null && _msgWindow.Handle != IntPtr.Zero) {
                NativeMethods.PostMessage(_msgWindow.Handle, Program.MsgShowSettings, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            ShowSettingsDirect();
        }

        public void ShowSettingsDirect() {
            if (_settingsForm == null || _settingsForm.IsDisposed) {
                _settingsForm = new SettingsForm(_config, _engine, OnConfigChanged);
            }
            _settingsForm.Show();
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            Logger.Log("SettingsForm shown directly.");
        }

        public void ToggleDock() {
            Logger.Log("ToggleDock called.");
            _config.Enabled = !_config.Enabled;
            _config.Save();
            UpdateMenuState();
            _engine.TriggerUpdate();
        }

        private void OnConfigChanged() {
            UpdateMenuState();
            _engine.TriggerUpdate();
        }

        private void Cleanup() {
            try {
                Logger.Log("Cleanup called.");
                if (_engine != null) {
                    _engine.Stop();
                    _engine = null;
                }
                if (_msgWindow != null) {
                    NativeMethods.UnregisterHotKey(_msgWindow.Handle, 1);
                    NativeMethods.UnregisterHotKey(_msgWindow.Handle, 2);
                    _msgWindow.DestroyHandle();
                    _msgWindow = null;
                }
                if (_notifyIcon != null) {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            } catch (Exception ex) {
                Logger.Log("Cleanup exception: " + ex);
            }
        }

        private class MessageWindow : NativeWindow {
            private DockAppContext _ctx;

            public MessageWindow(DockAppContext ctx) {
                _ctx = ctx;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m) {
                if (m.Msg == NativeMethods.WM_HOTKEY) {
                    _ctx.ToggleDock();
                    return;
                }
                if (Program.MsgShowSettings != 0 && (uint)m.Msg == Program.MsgShowSettings) {
                    Logger.Log("MessageWindow received MsgShowSettings.");
                    _ctx.ShowSettingsDirect();
                    return;
                }
                if (m.Msg == NativeMethods.WM_ENDSESSION || m.Msg == NativeMethods.WM_QUERYENDSESSION) {
                    _ctx.Cleanup();
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }
}
