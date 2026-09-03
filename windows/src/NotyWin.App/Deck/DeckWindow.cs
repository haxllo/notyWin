using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NotyWin.App.Deck;

/// <summary>
/// A borderless, no-activate, always-on-top tool window.
///
/// The macOS app uses NSPanel with <c>.borderless, .nonactivatingPanel</c>, level
/// <c>.statusBar</c> (over full-screen apps) or <c>.floating</c>. We map that to
/// a top-most popup with <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c>; the panel
/// is key-capable so the open note accepts keystrokes while another app is
/// frontmost.
/// </summary>
public sealed class DeckWindow : IDisposable
{
    private const string ClassName = "NotyWinDeck";

    private static bool _registered;

    public nint Hwnd { get; private set; }
    private DesktopWindowXamlSource? _xamlSource;

    public DeckWindow()
    {
        if (!_registered) RegisterClass();
        Hwnd = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
            ClassName,
            "",
            WS_POPUP | WS_VISIBLE,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        SetLayeredAlpha(Hwnd, 255);
    }

    /// <summary>
    /// Host a WinUI 3 <see cref="FrameworkElement"/> inside this HWND. Mirrors
    /// NSHostingView on macOS. The XAML island is bound for the lifetime of the
    /// window.
    /// </summary>
    public void Host(FrameworkElement element)
    {
        _xamlSource ??= new DesktopWindowXamlSource();
        WinRT.Interop.InitializeWithWindow.Initialize(_xamlSource, Hwnd);
        _xamlSource.Content = element;
    }

    public void ApplyLevel(bool overFullScreen)
    {
        var top = overFullScreen ? HWND_TOPMOST : HWND_TOP;
        SetWindowPos(Hwnd, top, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOFOCUS);
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        SetWindowPos(Hwnd, IntPtr.Zero,
            (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(w), (int)Math.Round(h),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    public void Show() => ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
    public void Hide() => ShowWindow(Hwnd, SW_HIDE);

    public void Dispose()
    {
        if (Hwnd != IntPtr.Zero)
        {
            DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
        _xamlSource?.Dispose();
        _xamlSource = null;
    }

    // P/Invoke

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_TOP = new(0);

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOFOCUS = 0x0008;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
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
        public IntPtr hIconSm;
    }

    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;

    private static IntPtr _wndProcStub;
    private static WndProcDelegate? _wndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void RegisterClass()
    {
        _wndProc = DefWndProc;
        _wndProcStub = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = _wndProcStub,
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx failed");
        _registered = true;
    }

    private static IntPtr DefWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hWnd, msg, wParam, lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private static void SetLayeredAlpha(IntPtr hWnd, byte alpha)
        => SetLayeredWindowAttributes(hWnd, 0, alpha, 0x02 /* LWA_ALPHA */);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}