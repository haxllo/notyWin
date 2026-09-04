using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace NotyWin.App.Deck;

/// <summary>
/// A borderless, no-activate, always-on-top tool window. Mirrors the macOS
/// <c>NSPanel(.borderless, .nonactivatingPanel)</c> at level <c>.statusBar</c>.
///
/// Chrome is managed through <see cref="OverlappedPresenter"/>. WinUI owns the
/// base HWND style; replacing it with <c>WS_POPUP</c> corrupts the XAML
/// composition target and can leave subsequent windows black.
///
/// Click-through on blank panel regions — the macOS <c>hitTest</c>-returns-nil
/// behaviour — comes from answering <c>WM_NCHITTEST</c> with
/// <c>HTTRANSPARENT</c> for any point that is not over a drawn item, so the
/// click falls through to whatever app is underneath. Points over an item
/// answer <c>HTCLIENT</c> and the window receives the button messages there.
///
/// Enter/exit is derived by the controller polling <c>GetCursorPos</c>, the
/// same approach the macOS app uses for its idle watch: the panel's hit
/// region changes shape on every state change, which makes
/// <c>WM_MOUSELEAVE</c> and tracking areas unreliable here.
/// </summary>
public sealed class DeckWindow
{
    public Microsoft.UI.Xaml.Window Window { get; }
    public AppWindow AppWindow { get; }
    public nint Hwnd { get; }

    /// <summary>Panel-local coordinates in DIPs, Y down.</summary>
    public event Action<double, double>? PointerMoved;
    public event Action<double, double>? LeftButtonDown;
    public event Action<double, double>? RightButtonDown;

    /// <summary>
    /// Decides whether a panel-local point is over a drawn item. Points that
    /// are not get <c>HTTRANSPARENT</c> so clicks pass to the app beneath.
    /// </summary>
    public Func<double, double, bool>? InteractiveFilter { get; set; }

    public double DpiScale => GetDpiForWindow(Hwnd) / 96.0;

    private readonly SubclassProc _subclassProc;

    public DeckWindow()
    {
        Window = new Microsoft.UI.Xaml.Window { Title = "Noty Deck" };
        Window.SystemBackdrop = null;
        AppWindow = Window.AppWindow;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsModal = false;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        // WS_EX_TOOLWINDOW keeps the deck out of Alt-Tab; WS_EX_NOACTIVATE is
        // the nonactivatingPanel half — clicking the deck never steals focus
        // from the app the user is in. The editor clears NOACTIVATE while a
        // note is open so a TextBox can take the keyboard.
        var ex = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(Hwnd, GWL_EXSTYLE, (IntPtr)ex);

        // Rooted in a field: a delegate held only by a local can be collected
        // while the native thunk is still registered, which crashes.
        _subclassProc = OnDeckMessage;
        SetWindowSubclass(Hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Toggle focus-stealing. True while a note editor is open.</summary>
    public void SetAcceptsActivation(bool accepts)
    {
        var ex = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
        ex = accepts ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(Hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <summary>
    /// Bring the panel to the foreground so the editor's TextBox can take the
    /// keyboard. The deck is <c>WS_EX_NOACTIVATE</c> while closed; the caller
    /// clears that first, then this makes the just-clicked window key.
    /// </summary>
    public void ActivateForInput() => SetForegroundWindow(Hwnd);

    public void ApplyLevel(bool overFullScreen)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = overFullScreen;
    }

    /// <summary>Physical-pixel screen rect of the panel.</summary>
    public (int Left, int Top, int Right, int Bottom) ScreenRect()
    {
        GetWindowRect(Hwnd, out var r);
        return (r.L, r.T, r.R, r.B);
    }

    /// <summary>Cursor position in physical screen pixels.</summary>
    public static (int X, int Y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Frame arrives in DIPs. Uses SetWindowPos (synchronous) instead of
    /// AppWindow.MoveAndResize (async) to avoid corrupting the XAML
    /// composition target when the canvas is invalidated mid-resize.
    /// </summary>
    public void SetFrame(double x, double y, double w, double h)
    {
        var scale = DpiScale;
        var px = (int)Math.Round(x * scale);
        var py = (int)Math.Round(y * scale);
        var pw = Math.Max(1, (int)Math.Round(w * scale));
        var ph = Math.Max(1, (int)Math.Round(h * scale));
        SetWindowPos(Hwnd, HWND_TOPMOST, px, py, pw, ph,
            SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public void Show() => ShowWindow(Hwnd, SW_SHOWNOACTIVATE);

    public void Hide() => ShowWindow(Hwnd, SW_HIDE);

    public void Dispose()
    {
        RemoveWindowSubclass(Hwnd, _subclassProc, IntPtr.Zero);
        Window.Close();
    }

    // MARK: - WndProc

    private IntPtr OnDeckMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                return new IntPtr(1);

            case WM_NCHITTEST:
                // lParam is in screen coordinates here.
                var (sx, sy) = LocalFromScreen(lParam);
                return InteractiveFilter?.Invoke(sx, sy) == true
                    ? new IntPtr(HTCLIENT)
                    : new IntPtr(HTTRANSPARENT);

            case WM_MOUSEMOVE:
                var (mx, my) = LocalFromClient(lParam);
                PointerMoved?.Invoke(mx, my);
                break;

            case WM_LBUTTONDOWN:
                var (lx, ly) = LocalFromClient(lParam);
                LeftButtonDown?.Invoke(lx, ly);
                break;

            case WM_RBUTTONDOWN:
                var (rx, ry) = LocalFromClient(lParam);
                RightButtonDown?.Invoke(rx, ry);
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private (double X, double Y) LocalFromClient(IntPtr lParam)
    {
        int px = (short)(lParam.ToInt64() & 0xFFFF);
        int py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var s = DpiScale;
        return (px / s, py / s);
    }

    private (double X, double Y) LocalFromScreen(IntPtr lParam)
    {
        int px = (short)(lParam.ToInt64() & 0xFFFF);
        int py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        GetWindowRect(Hwnd, out var r);
        var s = DpiScale;
        return ((px - r.L) / s, (py - r.T) / s);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData);

    // MARK: - P/Invoke

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr IDC_ARROW = new(32512);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
