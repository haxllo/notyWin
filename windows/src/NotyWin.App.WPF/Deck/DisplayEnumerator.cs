using System.Runtime.InteropServices;
using NotyWin.App.Geometry;

namespace NotyWin.App.Deck;

/// <summary>
/// Win32 implementation of <c>NSScreen.screens</c>. Enumerates display
/// monitors via <c>EnumDisplayMonitors</c> and resolves a display by id.
/// </summary>
public static class DisplayEnumerator
{
    /// <summary>Snapshot of every display, keyed by HMONITOR id.</summary>
    public static IReadOnlyDictionary<uint, DisplayRect> Snapshot()
    {
        var handle = GCHandle.Alloc(new MonitorEnumContext());
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                Marshal.GetFunctionPointerForDelegate(_enumProcDelegate), GCHandle.ToIntPtr(handle));
            return ((MonitorEnumContext)handle.Target!).Displays;
        }
        finally { handle.Free(); }
    }

    public static DisplayRect? DisplayAtPoint(double x, double y, IReadOnlyDictionary<uint, DisplayRect> displays)
    {
        DisplayRect? best = null;
        foreach (var d in displays.Values)
        {
            if (x >= d.FullLeft && x < d.FullRight && y >= d.FullTop && y < d.FullBottom)
                return d;
        }
        return best;
    }

    public static uint MainId()
    {
        var h = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        return unchecked((uint)h.ToInt64());
    }

    // P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    private sealed class MonitorEnumContext
    {
        public Dictionary<uint, DisplayRect> Displays { get; } = new();
    }

    private static bool EnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref info))
        {
            var id = unchecked((uint)hMonitor.ToInt64());
            var display = new DisplayRect(
                id,
                info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right, info.rcMonitor.bottom,
                info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom);
            ((MonitorEnumContext)GCHandle.FromIntPtr(dwData).Target!).Displays[id] = display;
        }
        return true;
    }

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    private static readonly MonitorEnumDelegate _enumProcDelegate = EnumProc;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, IntPtr lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
}