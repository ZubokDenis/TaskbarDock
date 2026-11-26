using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [DllImport("user32.dll")] static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static void Main() {
        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
        bool ok = SetThreadDesktop(hDesk);
        Console.WriteLine("SetThreadDesktop: " + ok);

        Process[] ps = Process.GetProcessesByName("TaskbarDock");
        if (ps.Length == 0) { Console.WriteLine("Not found"); return; }
        uint pid = (uint)ps[0].Id;
        Console.WriteLine("Process PID: " + pid);

        EnumWindows((hwnd, l) => {
            uint p;
            GetWindowThreadProcessId(hwnd, out p);
            if (p == pid) {
                StringBuilder sbText = new StringBuilder(128);
                GetWindowText(hwnd, sbText, 128);
                StringBuilder sbCls = new StringBuilder(128);
                GetClassName(hwnd, sbCls, 128);
                Console.WriteLine(string.Format("Found PID window: Hwnd={0:X}, Cls={1}, Text={2}, Vis={3}", hwnd.ToInt64(), sbCls.ToString(), sbText.ToString(), IsWindowVisible(hwnd)));
            }
            return true;
        }, IntPtr.Zero);
    }
}
