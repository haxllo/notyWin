namespace NotyWin.App.Models;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    /// <summary>Win32: Windows key. No macOS equivalent — surfaced as Cmd on the Mac side at registration time.</summary>
    Meta = 1 << 3,
}

/// <summary>
/// Engine-agnostic shortcut. Engine adapters translate <see cref="KeyCode"/>
/// (Win32 VK or Carbon key code) and the modifier flags into the native form.
/// Defaults match Sources/Settings.swift shortcuts at the engine layer — VK
/// tables are in <c>NotyWin.Storage</c> at registration time.
/// </summary>
public sealed class Shortcut
{
    public required KeyModifiers Modifiers { get; init; }
    /// <summary>Win32 virtual-key code. On macOS the same value is interpreted as a Carbon key code at registration time.</summary>
    public required int KeyCode { get; init; }

    public bool Matches(KeyModifiers mods, int keyCode) =>
        Modifiers == mods && KeyCode == keyCode;
}