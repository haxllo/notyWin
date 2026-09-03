using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NotyWin.App.Geometry;

namespace NotyWin.App.Deck;

/// <summary>
/// A borderless, no-activate, always-on-top tool window. Mirrors the macOS
/// NSPanel(<c>.borderless, .nonactivatingPanel</c>, level <c>.statusBar</c>).
///
/// The chrome is dropped by switching the HWND to <c>WS_POPUP</c>. We tried
/// <c>OverlappedPresenter.SetBorderAndTitleBar</c> + <c>ExtendsContentIntoTitleBar</c>
/// first, but the system frame always reserved 100+ pt for the close button
/// and a title bar. WS_POPUP gives us a true borderless pill.
///
/// Mouse input goes through a parallel invisible helper HWND — <c>WS_EX_TRANSPARENT</c>,
/// full screen height, the same x-position as the panel but a wider strip
/// (the hot zone). The helper is subclassed with <c>SetWindowSubclass</c>
/// from <c>comctl32</c> so the WndProc is reliable and we get
/// <c>WM_MOUSEMOVE</c> only when the cursor is over the helper. A global
/// <c>WH_MOUSE_LL</c> hook was tried first but it slows the whole system
/// down to a crawl because every mouse event across the OS is dispatched
/// to our process; the per-window subclass here only fires for the
/// pixels we care about.
/// </summary>
public sealed class DeckWindow
{
    public Microsoft.UI.Xaml.Window Window { get; }
    public AppWindow AppWindow { get; }
    public nint Hwnd { get; }
    public nint HelperHwnd { get; private set; }

    public event Action<double, double>? PointerMoved;
    public event Action? PointerEntered;
    public event Action? PointerExited;
    public event Action<int, int>? RightButtonDown;

    public DeckWindow()
    {
        Window = new Microsoft.UI.Xaml.Window { Title = "Noty Deck" };
        Window.SystemBackdrop = null;
        AppWindow = Window.AppWindow;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsModal = false;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        // WS_POPUP drops the 1-px OS border and the 100-pt space for the
        // close button. Set this BEFORE the first ShowWindow call.
        SetWindowLongPtr(Hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);

        CreateHelperWindow();
    }

    private void CreateHelperWindow()
    {
        var className = "NotyWinDeckHelper_" + Guid.NewGuid().ToString("N");
        var defWndProc = new WndProc(DefWindowProcHelper);
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(defWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassW(ref wc);

        // The helper covers the right edge of the screen. It's wide enough
        // to detect entry, and the coordinate-based exit check fires before
        // the cursor actually leaves the helper.
        int screenW = GetSystemMetrics(0); // SM_CXSCREEN
        int screenH = GetSystemMetrics(1); // SM_CYSCREEN
        const int helperW = 80;
        HelperHwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT,
            className, "", WS_POPUP | WS_VISIBLE,
            screenW - helperW, 0, helperW, screenH,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        SetWindowPos(HelperHwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _subclassProc = OnHelperMessage;
        SetWindowSubclass(HelperHwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);
    }

    public void ApplyLevel(bool overFullScreen)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = overFullScreen;
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(w), (int)Math.Round(h)));
        // The helper stays at the right-edge of the screen regardless of
        // where the panel is, so the hot zone always covers the screen
        // edge. (We could re-position it on every layout, but at rest the
        // panel and the right edge are the same x.)
    }

    public void Show()
    {
        const int SW_SHOWNOACTIVATE = 4;
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
        ShowWindow(HelperHwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        // Hide the helper too, so the cursor over the right edge doesn't
        // still trigger hover when the deck is in the "Dismissed" state.
        ShowWindow(HelperHwnd, SW_HIDE);
        AppWindow.Hide();
    }

    public void Dispose()
    {
        if (HelperHwnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(HelperHwnd, _subclassProc, IntPtr.Zero);
            DestroyWindow(HelperHwnd);
            HelperHwnd = IntPtr.Zero;
        }
    }

    // MARK: - Helper WndProc (ComCtl32 subclass)

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData);

    private SubclassProc? _subclassProc;
    private bool _isInside;
    private IntPtr _lastHwndUnder = IntPtr.Zero;

    private IntPtr OnHelperMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData)
    {
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_MOUSELEAVE = 0x02A3;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_SETCURSOR = 0x0020;

        if (msg == WM_SETCURSOR)
        {
            SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
            return new IntPtr(1);
        }

        if (msg == WM_MOUSEMOVE)
        {
            int x = (int)(short)(lParam.ToInt64() & 0xFFFF);
            int y = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
            PanelToLocal(x, y, out var lx, out var ly);

            // The panel is 14–30 px wide. When lx goes negative, the cursor
            // has moved left of the panel edge — treat that as "exited".
            // This works even with WS_EX_TRANSPARENT where WM_MOUSELEAVE
            // may not fire.
            bool inside = lx >= -2 && ly >= -2;

            if (inside && !_isInside)
            {
                _isInside = true;
                PointerEntered?.Invoke();
            }
            else if (!inside && _isInside)
            {
                _isInside = false;
                PointerExited?.Invoke();
            }

            if (inside)
                PointerMoved?.Invoke(lx, ly);
        }
        else if (msg == WM_MOUSELEAVE)
        {
            if (_isInside)
            {
                _isInside = false;
                PointerExited?.Invoke();
            }
        }
        else if (msg == WM_RBUTTONDOWN)
        {
            int x = (int)(short)(lParam.ToInt64() & 0xFFFF);
            int y = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
            PanelToLocal(x, y, out var lx, out var ly);
            RightButtonDown?.Invoke((int)lx, (int)ly);
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void PanelToLocal(int helperX, int helperY, out double localX, out double localY)
    {
        if (!GetWindowRect(Hwnd, out var panel))
        {
            localX = helperX; localY = helperY; return;
        }
        // Helper origin in screen coords: GetClientRect origin is the
        // helper's top-left. lParam is in helper client coords, which is
        // (helperX, helperY) in helper-screen-coords. Translate to panel
        // screen coords then to panel-local.
        if (GetWindowRect(HelperHwnd, out var helper))
        {
            int screenX = helper.L + helperX;
            int screenY = helper.T + helperY;
            localX = screenX - panel.L;
            localY = screenY - panel.T;
        }
        else
        {
            localX = helperX; localY = helperY;
        }
    }

    // MARK: - P/Invoke

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr DefWindowProcHelper(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hWnd, msg, wParam, lParam);

    // ComCtl32 SetWindowSubclass
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    private static readonly IntPtr IDC_ARROW = new(32512);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public int dwFlags;
        public IntPtr hwndTrack;
        public int dwHoverTime;
    }
}
