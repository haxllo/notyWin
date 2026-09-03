using System.Runtime.InteropServices;

namespace NotyWin.App.Deck;

/// <summary>
/// Watches for display reconfiguration. The macOS app listens for
/// <c>NSApplication.didChangeScreenParametersNotification</c>; Win32 equivalent
/// is the <c>WM_DISPLAYCHANGE</c> message broadcast to every top-level window.
/// </summary>
public sealed class DisplayChangeWatcher : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;

    public event Action? Changed;

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _wndProcStub;
    private IntPtr _hwnd;

    public DisplayChangeWatcher()
    {
        _wndProc = WndProc;
        _wndProcStub = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProcStub,
            hInstance = GetModuleHandle(null),
            lpszClassName = "NotyWinDisplayWatcher",
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(
            0, "NotyWinDisplayWatcher", "", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DISPLAYCHANGE) Changed?.Invoke();
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}