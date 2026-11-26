using System;
using System.IO;

namespace TaskbarDock {
    public static class Logger {
        private static string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        public static void Log(string msg) {
            try {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            } catch { }
        }
    }
}
