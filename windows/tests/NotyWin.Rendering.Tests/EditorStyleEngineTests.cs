using NotyWin.Rendering;
using Xunit;

namespace NotyWin.Rendering.Tests;

public class EditorStyleEngineTests
{
    private const int Ink = unchecked((int)0xFF202020);
    private const double BasePt = 10.125;
    private const char TasksDone = '\u2611';

    private static IReadOnlyList<EditorSpan> Style(
        string text, bool markdown = true, Func<string, bool>? completed = null) =>
        EditorStyleEngine.Style(
            text, 0, text.Length, Ink, BasePt, markdown,
            completed ?? (line => line.Length > 0 && line[0] == TasksDone));

    [Fact]
    public void Bold_MarksContentAndDimsMarkers()
    {
        var spans = Style("**hi**");
        var bold = spans.Single(s => s.Flags == EditorSpanFlags.Bold);
        Assert.Equal(2, bold.Start);
        Assert.Equal(2, bold.Length);
        Assert.Equal(0, bold.SizePt);
        Assert.Equal(2, spans.Count(s => s.ForeArgb == EditorStyleEngine.WithAlpha(Ink, 0.32)));
    }

    [Fact]
    public void Bold_UnderscoreDelimitersAlsoMatch()
    {
        var spans = Style("__hi__");
        Assert.Single(spans, s => s.Flags == EditorSpanFlags.Bold);
    }

    [Fact]
    public void Bold_InnerWhitespaceIsNotContent()
    {
        var spans = Style("** a **");
        Assert.Empty(spans.Where(s => s.Flags == EditorSpanFlags.Bold));
    }

    [Fact]
    public void Italic_MarksContentSingleDelimiter()
    {
        var spans = Style("*hi*");
        var italic = spans.Single(s => s.Flags == EditorSpanFlags.Italic);
        Assert.Equal(1, italic.Start);
        Assert.Equal(2, italic.Length);
    }

    [Fact]
    public void Italic_DoesNotFireInsideBold()
    {
        var spans = Style("**bold** and _em_");
        Assert.Single(spans.Where(s => s.Flags == EditorSpanFlags.Italic));
    }

    [Fact]
    public void Code_MonospaceWithBackground()
    {
        var spans = Style("`x = 1`");
        var code = spans.Single(s => (s.Flags & EditorSpanFlags.CodeBackground) != 0);
        Assert.Equal(1, code.Start);
        Assert.Equal(5, code.Length);
        Assert.Equal(EditorStyleEngine.MonoFontName, code.FontName);
    }

    [Fact]
    public void Struck_StrikesContent()
    {
        var spans = Style("~~gone~~");
        var struck = spans.Single(s => s.Flags == EditorSpanFlags.Strikethrough && s.Start == 2);
        Assert.Equal(4, struck.Length);
    }

    [Fact]
    public void Heading_SizeScalesWithLevelAndBoldsLine()
    {
        var spans = Style("# Title");
        var heading = spans.Single(s => (s.Flags & EditorSpanFlags.Bold) != 0 && s.SizePt > 0);
        Assert.Equal(BasePt + 5.9, heading.SizePt, 3);
        Assert.Equal(0, heading.Start);
        Assert.Equal(7, heading.Length);
    }

    [Fact]
    public void Heading_LevelSixClampsBump()
    {
        var spans = Style("###### Title");
        var heading = spans.Single(s => s.SizePt > 0);
        Assert.Equal(BasePt + 1.5, heading.SizePt, 3);
    }

    [Fact]
    public void Heading_RequiresSpaceAfterHashes()
    {
        Assert.Empty(Style("#NoSpace"));
    }

    [Fact]
    public void Quote_DimsLineItalicsContent()
    {
        var spans = Style("> quoted");
        var line = spans.First(s => s.ForeArgb == EditorStyleEngine.WithAlpha(Ink, 0.62));
        Assert.Equal(0, line.Start);
        Assert.Equal(8, line.Length);
        var content = spans.Single(s => s.Flags == EditorSpanFlags.Italic);
        Assert.Equal(2, content.Start);
        Assert.Equal(6, content.Length);
    }

    [Fact]
    public void Bullet_MarkerHalfInk()
    {
        var spans = Style("- item");
        var marker = spans.Single(s => s.ForeArgb == EditorStyleEngine.WithAlpha(Ink, 0.5));
        Assert.Equal(0, marker.Start);
        Assert.Equal(1, marker.Length);
    }

    [Fact]
    public void Bullet_RequiresSpace()
    {
        Assert.Empty(Style("*item"));
    }

    [Fact]
    public void Link_LabelUnderlinedWithOpenableUrl()
    {
        var spans = Style("[label](https://example.com)");
        var link = spans.Single(s => (s.Flags & EditorSpanFlags.Underline) != 0);
        Assert.Equal(1, link.Start);
        Assert.Equal(5, link.Length);
        Assert.Equal("https://example.com/", link.LinkUrl);
    }

    [Fact]
    public void Link_NonOpenableSchemeIsStyledButInert()
    {
        var spans = Style("[label](ftp://example.com)");
        var link = spans.Single(s => (s.Flags & EditorSpanFlags.Underline) != 0);
        Assert.Null(link.LinkUrl);
    }

    [Fact]
    public void PlainText_ProducesNoMarkdownSpans()
    {
        var spans = Style("just some words");
        Assert.Empty(spans);
    }

    [Fact]
    public void MarkdownDisabled_OnlyTasksStyled()
    {
        var spans = Style("**bold** and `code`\n\u2611 done", markdown: false);
        Assert.Empty(spans.Where(s => s.Flags != EditorSpanFlags.Strikethrough));
        Assert.Single(spans);
    }

    [Fact]
    public void CompletedTask_StrikesWholeLine()
    {
        var spans = Style("\u2611 done thing");
        var task = spans.Single(s => s.Flags == EditorSpanFlags.Strikethrough);
        Assert.Equal(0, task.Start);
        Assert.Equal(12, task.Length);
        Assert.Equal(EditorStyleEngine.WithAlpha(Ink, 0.45), task.ForeArgb);
    }

    [Fact]
    public void OpenTask_NotDimmed()
    {
        var spans = Style("\u2610 open thing");
        Assert.Empty(spans);
    }

    [Fact]
    public void TaskStyling_WinsOverMarkdownOnSameLine()
    {
        var spans = Style("\u2611 done **bold**");
        var task = spans.Last();
        Assert.Equal(EditorSpanFlags.Strikethrough, task.Flags);
        Assert.Equal(0, task.Start);
        Assert.Equal(15, task.Length);
    }

    [Fact]
    public void CarriageReturnLines_StyleLikeNewlineLines()
    {
        var spans = Style("**bold**\rplain\r\u2611 done");
        Assert.Single(spans.Where(s => s.Flags == EditorSpanFlags.Bold));
        Assert.Single(spans.Where(s => s.Flags == EditorSpanFlags.Strikethrough));
        var task = spans.Single(s => s.Flags == EditorSpanFlags.Strikethrough);
        Assert.Equal(15, task.Start);
    }

    [Fact]
    public void Style_OffsetsAreAbsolute()
    {
        var text = "plain line\n**bold**";
        var spans = EditorStyleEngine.Style(
            text, 11, 8, Ink, BasePt, true, _ => false);
        var bold = spans.Single(s => s.Flags == EditorSpanFlags.Bold);
        Assert.Equal(13, bold.Start);
    }

    // MARK: Line ranges

    [Fact]
    public void LineRangeContaining_MiddleLineIncludesTerminator()
    {
        var text = "aa\nbb\ncc";
        Assert.Equal((3, 3), EditorStyleEngine.LineRangeContaining(text, 4));
    }

    [Fact]
    public void LineRangeContaining_TrailingNewlineGivesEmptyFinalLine()
    {
        var text = "aa\n";
        Assert.Equal((3, 0), EditorStyleEngine.LineRangeContaining(text, 3));
    }

    [Fact]
    public void LineRangeContaining_ClampsLocation()
    {
        Assert.Equal((0, 3), EditorStyleEngine.LineRangeContaining("abc", 99));
    }

    [Fact]
    public void AffectedLineRanges_ExpandsEditToWholeLine()
    {
        var text = "aa\nbb\ncc";
        var ranges = EditorStyleEngine.AffectedLineRanges(text, new[] { (4, 0) });
        var r = Assert.Single(ranges);
        Assert.Equal((3, 3), r);
    }

    [Fact]
    public void AffectedLineRanges_NewlineEditTouchesBothParagraphs()
    {
        var text = "aa\nbb";
        var ranges = EditorStyleEngine.AffectedLineRanges(text, new[] { (2, 0) });
        var r = Assert.Single(ranges);
        Assert.Equal((0, 5), r);
    }

    [Fact]
    public void AffectedLineRanges_AdjacentLinesMerge()
    {
        var text = "aa\nbb\ncc\ndd";
        var ranges = EditorStyleEngine.AffectedLineRanges(text, new[] { (1, 0), (4, 0) });
        var r = Assert.Single(ranges);
        Assert.Equal((0, 6), r);
    }

    [Fact]
    public void AffectedLineRanges_DistantLinesStayDisjoint()
    {
        var text = "aa\nbb\ncc\ndd";
        var ranges = EditorStyleEngine.AffectedLineRanges(text, new[] { (1, 0), (7, 0) });
        Assert.Equal(2, ranges.Count);
        Assert.Equal((0, 3), ranges[0]);
        Assert.Equal((6, 3), ranges[1]);
    }

    [Fact]
    public void AffectedLineRanges_EmptyTextGivesZeroRange()
    {
        var ranges = EditorStyleEngine.AffectedLineRanges("", new[] { (0, 0) });
        var r = Assert.Single(ranges);
        Assert.Equal((0, 0), r);
    }

    [Fact]
    public void MergeRanges_OverlapsAndAdjacentMerge()
    {
        var merged = EditorStyleEngine.MergeRanges(
            new[] { (5, 3), (0, 2), (2, 4) }, 100);
        var r = Assert.Single(merged);
        Assert.Equal((0, 8), r);
    }

    [Fact]
    public void MergeRanges_DropsEmptyAndClamps()
    {
        var merged = EditorStyleEngine.MergeRanges(
            new[] { (0, 0), (10, 50), (-4, 2) }, 20);
        var r = Assert.Single(merged);
        Assert.Equal((10, 10), r);
    }

    // MARK: URLs and direction

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a b", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    public void OpenableUrl_OnlyOpenableSchemes(string raw, bool openable)
    {
        Assert.Equal(openable, EditorStyleEngine.OpenableUrl(raw) is not null);
    }

    [Fact]
    public void FirstStrongIsRtl_ArabicAndHebrewAreRtl()
    {
        Assert.True(EditorStyleEngine.FirstStrongIsRtl("مرحبا بالعالم"));
        Assert.True(EditorStyleEngine.FirstStrongIsRtl("שלום"));
        Assert.False(EditorStyleEngine.FirstStrongIsRtl("hello"));
        Assert.False(EditorStyleEngine.FirstStrongIsRtl("123 456"));
        Assert.False(EditorStyleEngine.FirstStrongIsRtl(""));
    }

    [Fact]
    public void FirstStrongIsRtl_NeutralDigitsDoNotDecide()
    {
        Assert.True(EditorStyleEngine.FirstStrongIsRtl("123 مرحبا"));
        Assert.False(EditorStyleEngine.FirstStrongIsRtl("123 hello"));
    }

    [Fact]
    public void LinksIn_ReportsLabelRangeAndUrl()
    {
        var links = EditorStyleEngine.LinksIn("see [docs](https://x.dev/a) and [ftp](ftp://x)");
        Assert.Equal(2, links.Count);
        Assert.Equal((5, 4, "https://x.dev/a"), links[0]);
        Assert.Null(links[1].Url);
    }
}
