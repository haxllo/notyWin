using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NotyWin.App.Geometry;

namespace NotyWin.App.Deck;

/// <summary>
/// A borderless, no-activate, always-on-top tool window.
///
/// The macOS app uses NSPanel with <c>.borderless, .nonactivatingPanel</c>, level
/// <c>.statusBar</c> (over full-screen apps) or <c>.floating</c>. We model the
/// same surface as a WinUI 3 <see cref="Window"/> with an
/// <c>OverlappedPresenter</c> configured to drop the system frame and sit
/// above the topmost z-band.
///
/// The chrome-less appearance comes from
/// <c>AppWindow.TitleBar.ExtendsContentIntoTitleBar = true</c> plus a
/// zero-height title bar — the WinUI 3 supported way to drop the system
/// frame. Without this, OverlappedPresenter draws a 30-pt title bar that
/// ruins the visual.
///
/// Pointer enter/exit is handled at the HWND level via
/// <see cref="TrackMouseEvent"/>; WinUI's managed pointer events don't fire
/// on a non-foreground borderless window, so we use the raw
/// <c>WM_MOUSEMOVE</c>/<c>WM_MOUSELEAVE</c> path on the window's HWND.
/// </summary>
public sealed class DeckWindow
{
    public Microsoft.UI.Xaml.Window Window { get; }
    public AppWindow AppWindow { get; }
    public nint Hwnd { get; }

    public event Action<double, double>? PointerMoved;
    public event Action? PointerEntered;
    public event Action? PointerExited;
    public event Action<int, int>? RightButtonDown;

    public DeckWindow()
    {
        Window = new Microsoft.UI.Xaml.Window
        {
            Title = "Noty Deck",
        };
        AppWindow = Window.AppWindow;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);   // border yes, title bar no
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsModal = false;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        _instanceProc = InstanceWndProc;
        InstallHook();
    }

    public void ApplyLevel(bool overFullScreen)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = overFullScreen;
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        // Drop the OS chrome completely by rewriting the window style to
        // WS_POPUP. OverlappedPresenter (the default WinUI 3 border) reserves
        // 100+ pt for the system buttons + non-client area; the deck only
        // needs to draw the pill. This is the same trick the SwiftUI app gets
        // for free on macOS via .borderless.
        SetWindowLongPtr(Hwnd, GWL_STYLE,
            WS_POPUP | WS_VISIBLE);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(w), (int)Math.Round(h)));
    }

    public void Show() => Window.Activate();
    public void Hide() => AppWindow.Hide();

    // MARK: - Raw input hook (Win32)

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int WM_RBUTTONDOWN = 0x0204;

    private const int TME_HOVER = 0x00000001;
    private const int TME_LEAVE = 0x00000002;
    private const int TME_NONCLIENT = 0x00000010;
    private const uint TME_CANCEL = 0x80000000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProcDelegate _instanceProc;
    private IntPtr _prevWndProc = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    private const int GWLP_WNDPROC = -4;
    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;

    private void InstallHook()
    {
        // Create the per-instance delegate (not the static one above) so the
        // trampoline resolves to this window's events.
        var stub = Marshal.GetFunctionPointerForDelegate(_instanceProc);
        var prev = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, stub);
        _prevWndProc = prev;
    }

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_MOUSEMOVE)
        {
            int x = (int)(short)(lParam.ToInt64() & 0xFFFF);
            int y = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
            PointerMoved?.Invoke(x, y);
            StartTrack();
        }
        else if (msg == WM_MOUSELEAVE)
        {
            PointerExited?.Invoke();
        }
        else if (msg == WM_RBUTTONDOWN)
        {
            int x = (int)(short)(lParam.ToInt64() & 0xFFFF);
            int y = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
            RightButtonDown?.Invoke(x, y);
        }
        if (_prevWndProc != IntPtr.Zero)
            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool _tracking;
    private void StartTrack()
    {
        if (_tracking) return;
        _tracking = true;
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = Hwnd,
            dwHoverTime = 0,
        };
        TrackMouseEvent(ref tme);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
}