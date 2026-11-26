using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [DllImport("user32.dll")] static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static void Main() {
        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
        SetThreadDesktop(hDesk);

        Process[] ps = Process.GetProcessesByName("TaskbarDock");
        if (ps.Length == 0) { Console.WriteLine("Not found"); return; }
        uint pid = (uint)ps[0].Id;
        Console.WriteLine("Process PID: " + pid);

        EnumDesktopWindows(hDesk, (hwnd, l) => {
            uint p;
            GetWindowThreadProcessId(hwnd, out p);
            if (p == pid) {
                StringBuilder sb = new StringBuilder(64);
                GetClassName(hwnd, sb, 64);
                Console.WriteLine(string.Format("Window: {0:X}, Class: {1}, Visible: {2}", hwnd.ToInt64(), sb.ToString(), IsWindowVisible(hwnd)));
            }
            return true;
        }, IntPtr.Zero);
    }
}
