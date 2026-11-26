using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(int dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

    static void Main() {
        Process p = Process.GetProcessById(53952);
        foreach (ProcessThread t in p.Threads) {
            Console.WriteLine("Thread " + t.Id);
            EnumThreadWindows(t.Id, (hwnd, l) => {
                StringBuilder sb = new StringBuilder(64);
                GetClassName(hwnd, sb, 64);
                Console.WriteLine(string.Format("  Hwnd: {0:X}, Class: {1}, Vis: {2}", hwnd.ToInt64(), sb.ToString(), IsWindowVisible(hwnd)));
                return true;
            }, IntPtr.Zero);
        }
    }
}
