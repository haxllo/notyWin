using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NotyWin.Rendering;

namespace NotyWin.App.Deck;

/// <summary>
/// WPF implementation of <see cref="ITextMeasurer"/> using
/// <see cref="FormattedText"/>. Replaces the WinForms GdiTextMeasurer.
/// </summary>
public sealed class WpfTextMeasurer : ITextMeasurer
{
    public double MeasureWidth(string text, string fontFamily, double fontSize, double tracking = 0)
    {
        var typeface = new Typeface(fontFamily);
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black);
        return formatted.WidthIncludingTrailingWhitespace + tracking * Math.Max(0, text.Length - 1);
    }
}
