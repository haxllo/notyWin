# Dev-only: move the cursor (and optionally click) so deck states can be
# exercised without a human at the keyboard.
# Usage: powershell -File mouse.ps1 -X 2500 -Y 700 [-Click]
param([int]$X, [int]$Y, [switch]$Click)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeMouse {
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
}
"@
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $X, $Y
Start-Sleep -Milliseconds 250
if ($Click) {
    [NativeMouse]::mouse_event([NativeMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    [NativeMouse]::mouse_event([NativeMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}
Write-Output "cursor at $X,$Y click=$Click"
