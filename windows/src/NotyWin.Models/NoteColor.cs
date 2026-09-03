namespace NotyWin.App.Models;

/// <summary>
/// Note paper / dash / ink colour. Identical palette to <c>NoteColor.all</c> in
/// Sources/Core.swift, expressed as 0xRRGGBB ints (alpha 0xFF). The names are
/// stable archive values, matching the Swift app exactly.
/// </summary>
public sealed class NoteColor
{
    public required string Name { get; init; }
    public required int PaperArgb { get; init; }
    public required int DashArgb { get; init; }
    public required int InkArgb { get; init; }

    public static readonly NoteColor[] All =
    {
        new() { Name = "Lemon",  PaperArgb = unchecked((int)0xFFFCE795), DashArgb = unchecked((int)0xFFE0AD08), InkArgb = unchecked((int)0xFF3A3008) },
        new() { Name = "Peach",  PaperArgb = unchecked((int)0xFFFBCFA6), DashArgb = unchecked((int)0xFFE2762A), InkArgb = unchecked((int)0xFF422413) },
        new() { Name = "Rose",   PaperArgb = unchecked((int)0xFFFAC4D1), DashArgb = unchecked((int)0xFFDC4570), InkArgb = unchecked((int)0xFF40161F) },
        new() { Name = "Lilac",  PaperArgb = unchecked((int)0xFFD9C7FA), DashArgb = unchecked((int)0xFF7C4DEE), InkArgb = unchecked((int)0xFF2A1B44) },
        new() { Name = "Sky",    PaperArgb = unchecked((int)0xFFBEDDFA), DashArgb = unchecked((int)0xFF2280D6), InkArgb = unchecked((int)0xFF13293A) },
        new() { Name = "Mint",   PaperArgb = unchecked((int)0xFFB4E8D0), DashArgb = unchecked((int)0xFF0E9B6E), InkArgb = unchecked((int)0xFF0F2E23) },
        new() { Name = "Sand",   PaperArgb = unchecked((int)0xFFE3D3B4), DashArgb = unchecked((int)0xFFA37B3C), InkArgb = unchecked((int)0xFF372C18) },
        new() { Name = "Slate",  PaperArgb = unchecked((int)0xFFCBD6E2), DashArgb = unchecked((int)0xFF4E6579), InkArgb = unchecked((int)0xFF1A242E) },
    };

    public static NoteColor At(int i)
    {
        var count = All.Length;
        return All[((i % count) + count) % count];
    }
}