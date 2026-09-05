using System.Runtime.InteropServices;

namespace NotyWin.App.Deck;

/// <summary>
/// System tray icon using <c>Shell_NotifyIcon</c>. Shows a Noty icon in the
/// notification area with a right-click context menu: New Note, All Notes,
/// Archive, separator, Settings, Quit. Keeps the app alive when all deck
/// windows are hidden.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x0400 + 1;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int IDI_APPLICATION = 32512;

    private nint _hwnd;
    private nint _icon;
    private bool _disposed;
    private readonly WndProcDelegate _wndProc;

    public Action? OnNewNote { get; set; }
    public Action? OnAllNotes { get; set; }
    public Action? OnArchive { get; set; }
    public Action? OnSettings { get; set; }
    public Action? OnQuit { get; set; }

    public TrayIcon()
    {
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "NotyTray_" + Guid.NewGuid().ToString("N"),
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, 0, 0, 0, 0);

        _icon = LoadIcon(IntPtr.Zero, (nint)IDI_APPLICATION);

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = "Noty",
        };
        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            else if (mouseMsg == WM_LBUTTONUP)
            {
                OnNewNote?.Invoke();
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        var idx = 0u;
        InsertMenu(hMenu, idx++, 0, 1, "New Note");
        InsertMenu(hMenu, idx++, 0, 2, "All Notes");
        InsertMenu(hMenu, idx++, 0, 3, "Archive");
        InsertMenu(hMenu, idx++, 0x00000800, 0, ""); // MF_SEPARATOR
        InsertMenu(hMenu, idx++, 0, 4, "Settings");
        InsertMenu(hMenu, idx++, 0x00000800, 0, ""); // MF_SEPARATOR
        InsertMenu(hMenu, idx++, 0, 5, "Quit");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenu(hMenu, 0x0100, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero); // TPM_RETURNCMD
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case 1: OnNewNote?.Invoke(); break;
            case 2: OnAllNotes?.Invoke(); break;
            case 3: OnArchive?.Invoke(); break;
            case 4: OnSettings?.Invoke(); break;
            case 5: OnQuit?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
    }

    // P/Invoke
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(nint hMenu, uint uPosition, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern nint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
