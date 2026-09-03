using System.Runtime.InteropServices;

namespace NotyWin.App.Deck;

/// <summary>
/// Registers global hotkeys via <c>RegisterHotKey</c> and dispatches them to
/// actions. Mirrors <c>HotKeys</c> in Sources/HotKeys.swift (Carbon
/// <c>RegisterEventHotKey</c>). No Accessibility permission is required on
/// either platform.
///
/// A hidden message-only window receives <c>WM_HOTKEY</c>; the four global
/// shortcuts (new note, all notes, archive, quick capture) are registered from
/// the current <see cref="NotyWin.App.Models.SettingsSnapshot"/>.
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;

    private nint _hwnd;
    private bool _disposed;
    private readonly WndProcDelegate _wndProc;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public Action? OnNewNote { get; set; }
    public Action? OnAllNotes { get; set; }
    public Action? OnCapture { get; set; }
    public Action? OnArchive { get; set; }

    public GlobalHotKeys()
    {
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "NotyHotKey_" + Guid.NewGuid().ToString("N"),
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, (nint)HWND_MESSAGE, 0, 0, 0);
    }

    /// <summary>Register the four global shortcuts from the current settings.</summary>
    public void RegisterFromSettings(Models.SettingsSnapshot s)
    {
        UnregisterAll();
        Register(s.ScNewNote.Modifiers, s.ScNewNote.KeyCode, () => OnNewNote?.Invoke());
        Register(s.ScAllNotes.Modifiers, s.ScAllNotes.KeyCode, () => OnAllNotes?.Invoke());
        Register(s.ScCapture.Modifiers, s.ScCapture.KeyCode, () => OnCapture?.Invoke());
        Register(s.ScArchive.Modifiers, s.ScArchive.KeyCode, () => OnArchive?.Invoke());
    }

    private void Register(Models.KeyModifiers mods, int vk, Action action)
    {
        var id = _nextId++;
        var nativeMods = ToNativeModifiers(mods);
        if (RegisterHotKey(_hwnd, id, nativeMods, (uint)vk))
            _actions[id] = action;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
                action();
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static uint ToNativeModifiers(Models.KeyModifiers mods)
    {
        uint result = 0;
        if (mods.HasFlag(Models.KeyModifiers.Shift)) result |= 0x0004; // MOD_SHIFT
        if (mods.HasFlag(Models.KeyModifiers.Control)) result |= 0x0002; // MOD_CONTROL
        if (mods.HasFlag(Models.KeyModifiers.Alt)) result |= 0x0001; // MOD_ALT
        if (mods.HasFlag(Models.KeyModifiers.Meta)) result |= 0x0008; // MOD_WIN
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
