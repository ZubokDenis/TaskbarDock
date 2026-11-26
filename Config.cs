using System;
using System.IO;
using Microsoft.Win32;

namespace TaskbarDock {
    public class Config {
        public bool Enabled = true;
        public int Mode = 0; // 0 = Only Dock (Apps), 1 = Split Dock (Apps + Tray)
        public int CornerRadius = 16;
        public int PaddingHorizontal = 12;
        public int MarginBottom = 4;
        public int MarginTop = 4;
        public int TaskbarOpacity = 235; // 100..255 (235 = ~92%: transparent glass, 100% bright vibrant icons)
        public bool FloatOverWindows = true;
        public bool AutoHideInFullscreen = true;
        public bool ShowQuickSettingsButton = true;
        public bool AutoStart = false;
        public int PollIntervalMs = 200;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "TaskbarDock";

        private static string ConfigPath {
            get {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(appDir, "config.ini");
            }
        }

        public static Config Load() {
            Config cfg = new Config();
            cfg.AutoStart = CheckAutoStart();

            string path = ConfigPath;
            if (!File.Exists(path)) {
                cfg.Save();
                return cfg;
            }

            try {
                string[] lines = File.ReadAllLines(path);
                foreach (string rawLine in lines) {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    string key = line.Substring(0, eqIdx).Trim();
                    string val = line.Substring(eqIdx + 1).Trim();
                    int hashIdx = val.IndexOf('#');
                    if (hashIdx >= 0) val = val.Substring(0, hashIdx).Trim();
                    int semiIdx = val.IndexOf(';');
                    if (semiIdx >= 0) val = val.Substring(0, semiIdx).Trim();

                    switch (key.ToLowerInvariant()) {
                        case "enabled":
                            bool.TryParse(val, out cfg.Enabled);
                            break;
                        case "mode":
                            int.TryParse(val, out cfg.Mode);
                            break;
                        case "cornerradius":
                            int.TryParse(val, out cfg.CornerRadius);
                            break;
                        case "paddinghorizontal":
                            int.TryParse(val, out cfg.PaddingHorizontal);
                            break;
                        case "marginbottom":
                        case "marginvertical":
                            int.TryParse(val, out cfg.MarginBottom);
                            break;
                        case "margintop":
                            int.TryParse(val, out cfg.MarginTop);
                            break;
                        case "taskbaropacity":
                            int.TryParse(val, out cfg.TaskbarOpacity);
                            break;
                        case "floatoverwindows":
                            bool.TryParse(val, out cfg.FloatOverWindows);
                            break;
                        case "autohideinfullscreen":
                            bool.TryParse(val, out cfg.AutoHideInFullscreen);
                            break;
                        case "showquicksettingsbutton":
                            bool.TryParse(val, out cfg.ShowQuickSettingsButton);
                            break;
                        case "pollintervalms":
                            int.TryParse(val, out cfg.PollIntervalMs);
                            break;
                        case "autostart":
                            bool.TryParse(val, out cfg.AutoStart);
                            break;
                    }
                }
            } catch { }

            // Clamps
            if (cfg.CornerRadius < 0) cfg.CornerRadius = 0;
            if (cfg.CornerRadius > 35) cfg.CornerRadius = 35;
            if (cfg.PaddingHorizontal < 0) cfg.PaddingHorizontal = 0;
            if (cfg.PaddingHorizontal > 40) cfg.PaddingHorizontal = 40;
            if (cfg.MarginBottom < 0) cfg.MarginBottom = 0;
            if (cfg.MarginBottom > 16) cfg.MarginBottom = 16;
            if (cfg.MarginTop < 0) cfg.MarginTop = 0;
            if (cfg.MarginTop > 10) cfg.MarginTop = 10;
            if (cfg.TaskbarOpacity < 100) cfg.TaskbarOpacity = 100;
            if (cfg.TaskbarOpacity > 255) cfg.TaskbarOpacity = 255;
            if (cfg.Mode < 0 || cfg.Mode > 1) cfg.Mode = 0;

            return cfg;
        }

        public void Save() {
            try {
                using (StreamWriter sw = new StreamWriter(ConfigPath, false)) {
                    sw.WriteLine("# TaskbarDock Configuration");
                    sw.WriteLine("Enabled=" + Enabled);
                    sw.WriteLine("Mode=" + Mode + " # 0 = Apps Only, 1 = Split Dock");
                    sw.WriteLine("CornerRadius=" + CornerRadius);
                    sw.WriteLine("PaddingHorizontal=" + PaddingHorizontal);
                    sw.WriteLine("MarginBottom=" + MarginBottom + " # distance from bottom of screen (px)");
                    sw.WriteLine("MarginTop=" + MarginTop);
                    sw.WriteLine("TaskbarOpacity=" + TaskbarOpacity + " # 100..255 (230 = ~90% glass)");
                    sw.WriteLine("FloatOverWindows=" + FloatOverWindows);
                    sw.WriteLine("AutoHideInFullscreen=" + AutoHideInFullscreen);
                    sw.WriteLine("ShowQuickSettingsButton=" + ShowQuickSettingsButton);
                    sw.WriteLine("PollIntervalMs=" + PollIntervalMs);
                    sw.WriteLine("AutoStart=" + AutoStart);
                }
            } catch { }

            SetAutoStart(AutoStart);
        }

        public static bool CheckAutoStart() {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false)) {
                    if (key != null) {
                        object val = key.GetValue(AppName);
                        return val != null;
                    }
                }
            } catch { }
            return false;
        }

        public static void SetAutoStart(bool enable) {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)) {
                    if (key != null) {
                        if (enable) {
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            key.SetValue(AppName, "\"" + exePath + "\"");
                        } else {
                            key.DeleteValue(AppName, false);
                        }
                    }
                }
            } catch { }
        }
    }
}
