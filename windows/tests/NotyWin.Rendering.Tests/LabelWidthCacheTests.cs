using NotyWin.Rendering;
using Xunit;

namespace NotyWin.Rendering.Tests;

public class LabelWidthCacheTests
{
    private sealed class StubMeasurer : ITextMeasurer
    {
        public double MeasureWidth(string text, string fontFamily, double fontSize, double trackingPerChar)
            => text.Length * 4.0 + trackingPerChar * text.Length;
    }

    [Fact]
    public void Width_CachesResults()
    {
        var cache = new LabelWidthCache(new StubMeasurer());
        var a = cache.Width("hello");
        var b = cache.Width("hello");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Width_UppercasesText()
    {
        // The Swift port uppercases before measuring so cache key matches.
        var cache = new LabelWidthCache(new StubMeasurer());
        Assert.Equal(cache.Width("hello"), cache.Width("HELLO"));
    }

    [Fact]
    public void Width_AddsTrackingPerChar()
    {
        var cache = new LabelWidthCache(new StubMeasurer()) { TrackingPerChar = 0.5 };
        // Stub returns text.Length * 4 + trackingPerChar * text.Length
        // text "abc" → 12 + 1.5 = 13.5
        Assert.Equal(13.5, cache.Width("abc"));
    }

    [Fact]
    public void Longest_PicksWidest()
    {
        var cache = new LabelWidthCache(new StubMeasurer());
        var widths = new[] { "a", "longer", "no" }.Select(cache.Width).ToList();
        Assert.Equal(widths.Max(), cache.Width("longer"));
    }
}