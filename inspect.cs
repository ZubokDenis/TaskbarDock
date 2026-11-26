using System;
using System.Windows.Automation;

class Program {
    static void Main() {
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var elem = AutomationElement.FromHandle(tray);
        var children = elem.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        Console.WriteLine("Total children: " + children.Count);
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        foreach (AutomationElement ch in children) {
            try {
                string aid = ch.Current.AutomationId;
                string cls = ch.Current.ClassName;
                var rect = ch.Current.BoundingRectangle;
                bool isTaskItem = (
                    aid == "StartButton" ||
                    aid == "SearchButton" ||
                    aid == "WidgetsButton" ||
                    aid == "TaskViewButton" ||
                    (cls != null && cls.IndexOf("TaskListButton", StringComparison.OrdinalIgnoreCase) >= 0)
                );
                if (isTaskItem && rect.Width > 0 && rect.Height > 0) {
                    Console.WriteLine(string.Format("{0} [{1}]: {2}, {3}, {4}x{5}", aid, cls, rect.Left, rect.Top, rect.Width, rect.Height));
                    minX = Math.Min(minX, rect.Left);
                    maxX = Math.Max(maxX, rect.Right);
                }
            } catch {}
        }
        Console.WriteLine(string.Format("minX={0}, maxX={1}, span={2}", minX, maxX, maxX - minX));
    }
}
class NativeMethods {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
}
