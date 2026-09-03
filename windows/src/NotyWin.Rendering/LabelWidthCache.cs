using System.Globalization;
using NotyWin.App.Models;

namespace NotyWin.Rendering;

/// <summary>
/// Engine-agnostic text measurement. Default impl uses <c>System.Windows.Forms.TextRenderer</c>
/// (System.Drawing). Tests inject a fake.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>Returns the rendered width of <paramref name="text"/> in pixels at <paramref name="fontSize"/> pt.</summary>
    double MeasureWidth(string text, string fontFamily, double fontSize, double trackingPerChar);
}

public sealed class GdiTextMeasurer : ITextMeasurer
{
    public double MeasureWidth(string text, string fontFamily, double fontSize, double trackingPerChar)
    {
        using var font = new System.Drawing.Font(fontFamily, (float)fontSize, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
        var flags = System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.SingleLine;
        var proposed = new System.Drawing.Size(int.MaxValue, int.MaxValue);
        var size = System.Windows.Forms.TextRenderer.MeasureText(text, font, proposed, flags);
        // Tracking is added per character in the Swift port; do the same.
        return size.Width + trackingPerChar * text.Length;
    }
}

/// <summary>
/// Memoized label-width measurement. The Swift port keeps a 400-entry rolling
/// cache keyed on (fontName, pointSize, text) because every layout pass reads
/// widths on every tab. Same shape here.
/// </summary>
public sealed class LabelWidthCache
{
    private const int Cap = 400;
    private readonly Dictionary<string, double> _cache = new();
    private readonly ITextMeasurer _measurer;

    public LabelWidthCache(ITextMeasurer measurer) { _measurer = measurer; }

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 9.5;
    public double TrackingPerChar { get; set; } = 0.1;

    public double Width(string title)
    {
        var key = $"{FontFamily}|{FontSize.ToString(CultureInfo.InvariantCulture)}|{title}";
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var upper = title.ToUpperInvariant();
        var w = _measurer.MeasureWidth(upper, FontFamily, FontSize, TrackingPerChar);
        if (_cache.Count > Cap) _cache.Clear();
        _cache[key] = w;
        return w;
    }

    public void Clear() => _cache.Clear();
}

/// <summary>
/// Helper: returns the widest label across a set of titles, with the same
/// face used to render tabs. Mirrors DeckRootView.longestLabel.
/// </summary>
public static class Labels
{
    public static double Longest(IEnumerable<Note> notes, LabelWidthCache cache, string untitledLabel)
        => notes.Select(n => n.DisplayTitle(untitledLabel)).Select(cache.Width).DefaultIfEmpty(0).Max();
}