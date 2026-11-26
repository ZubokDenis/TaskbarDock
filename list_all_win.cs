using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static void Main() {
        Process[] ps = Process.GetProcessesByName("TaskbarDock");
        if (ps.Length == 0) { Console.WriteLine("Not found"); return; }
        uint pid = (uint)ps[0].Id;
        Console.WriteLine("Process PID: " + pid);

        EnumWindows((hwnd, l) => {
            uint p;
            GetWindowThreadProcessId(hwnd, out p);
            if (p == pid) {
                StringBuilder sb = new StringBuilder(64);
                GetClassName(hwnd, sb, 64);
                RECT r;
                GetWindowRect(hwnd, out r);
                Console.WriteLine(string.Format("Window: {0:X}, Class: {1}, Rect: ({2},{3})-({4},{5}), Vis: {6}", hwnd.ToInt64(), sb.ToString(), r.Left, r.Top, r.Right, r.Bottom, IsWindowVisible(hwnd)));
            }
            return true;
        }, IntPtr.Zero);
    }
}
