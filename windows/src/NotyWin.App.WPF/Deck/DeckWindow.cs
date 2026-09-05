using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;

namespace NotyWin.App.Deck;

/// <summary>
/// WPF transparent overlay window for the deck. Uses AllowsTransparency="True"
/// for true transparent background — no WS_POPUP hacks, no XAML composition
/// target corruption. The macOS equivalent is NSPanel(.borderless,
/// .nonactivatingPanel, .clear).
/// </summary>
public sealed class DeckWindow : IDisposable
{
    public Window Window { get; }
    public nint Hwnd { get; private set; }

    public event Action<double, double>? PointerMoved;
    public event Action<double, double>? LeftButtonDown;
    public event Action<double, double>? RightButtonDown;

    public Func<double, double, bool>? InteractiveFilter { get; set; }
    public double DpiScale { get; private set; } = 1.0;

    private HwndSource? _hwndSource;
    private HwndSourceHook? _wndProcHook;
    private bool _disposed;

    public DeckWindow()
    {
        Window = new Window
        {
            Title = "Noty Deck",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Manual,
        };

        Window.Loaded += (_, _) =>
        {
            var helper = new WindowInteropHelper(Window);
            Hwnd = helper.Handle;
            DpiScale = GetDpiForWindow(Hwnd) / 96.0;

            // Remove from Alt-Tab and make non-activating.
            var ex = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64()
                | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(Hwnd, GWL_EXSTYLE, (IntPtr)ex);

            // Subclass for hit-testing and mouse messages.
            _hwndSource = HwndSource.FromHwnd(Hwnd);
            _wndProcHook = WndProc;
            _hwndSource?.AddHook(_wndProcHook);
        };
    }

    public void SetAcceptsActivation(bool accepts)
    {
        if (Hwnd == 0) return;
        var ex = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
        ex = accepts ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(Hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    public void ActivateForInput()
    {
        if (Hwnd != 0) SetForegroundWindow(Hwnd);
    }

    public void ApplyLevel(bool overFullScreen)
    {
        Window.Topmost = overFullScreen;
    }

    public (int Left, int Top, int Right, int Bottom) ScreenRect()
    {
        if (Hwnd == 0) return (0, 0, 0, 0);
        GetWindowRect(Hwnd, out var r);
        return (r.L, r.T, r.R, r.B);
    }

    public static (int X, int Y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Set window position and size. Frame arrives in DIPs.</summary>
    public void SetFrame(double x, double y, double w, double h)
    {
        var scale = DpiScale;
        var px = (int)Math.Round(x * scale);
        var py = (int)Math.Round(y * scale);
        var pw = Math.Max(1, (int)Math.Round(w * scale));
        var ph = Math.Max(1, (int)Math.Round(h * scale));
        if (Hwnd != 0)
            SetWindowPos(Hwnd, HWND_TOPMOST, px, py, pw, ph,
                SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public void Show() => Window.Show();

    public void Hide() => Window.Hide();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_wndProcHook is not null)
            _hwndSource?.RemoveHook(_wndProcHook);
        Window.Close();
    }

    // WndProc for hit-testing and mouse messages.
    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case WM_NCHITTEST:
                var (sx, sy) = LocalFromScreen(lParam);
                if (InteractiveFilter?.Invoke(sx, sy) == true)
                {
                    handled = false; // Let WPF handle it as HTCLIENT.
                    return IntPtr.Zero;
                }
                handled = true;
                return new IntPtr(HTTRANSPARENT);

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
        handled = false;
        return IntPtr.Zero;
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

    // P/Invoke
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
