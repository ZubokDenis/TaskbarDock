using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static void Main() {
        EnumWindows((hwnd, l) => {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try {
                Process p = Process.GetProcessById((int)pid);
                if (p.ProcessName.IndexOf("TaskbarDock", StringComparison.OrdinalIgnoreCase) >= 0) {
                    StringBuilder sbText = new StringBuilder(128);
                    GetWindowText(hwnd, sbText, 128);
                    StringBuilder sbCls = new StringBuilder(128);
                    GetClassName(hwnd, sbCls, 128);
                    Console.WriteLine(string.Format("Hwnd={0:X}, Pid={1}, Cls={2}, Text={3}", hwnd.ToInt64(), pid, sbCls.ToString(), sbText.ToString()));
                }
            } catch {}
            return true;
        }, IntPtr.Zero);
    }
}
