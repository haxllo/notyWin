using System;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static int Main(string[] args)
    {
        uint targetPid = 0;
        if (args.Length > 0) uint.TryParse(args[0], out targetPid);
        EnumWindows((h, l) =>
        {
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            if (targetPid != 0 && pid != targetPid) return true;
            if (!IsWindowVisible(h)) return true;
            var cn = new StringBuilder(256);
            GetClassName(h, cn, 256);
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            RECT r;
            GetWindowRect(h, out r);
            int exs = GetWindowLong(h, -20);
            Console.WriteLine($"h={h} class={cn} title='{sb}' rect={r.L},{r.T}-{r.R},{r.B} (w={r.R - r.L},h={r.B - r.T}) exStyle=0x{exs:X8}");
            return true;
        }, IntPtr.Zero);
        return 0;
    }
}
