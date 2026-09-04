using System.Text.RegularExpressions;

namespace NotyWin.Rendering;

[Flags]
public enum EditorSpanFlags
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Strikethrough = 1 << 2,
    Underline = 1 << 3,
    CodeBackground = 1 << 4,
}

/// <summary>
/// One character-formatting instruction over an absolute range of the note
/// body. <see cref="SizePt"/> of 0 inherits the base size; a null
/// <see cref="FontName"/> inherits the body font; <see cref="ForeArgb"/> of 0
/// inherits the note ink.
/// </summary>
public sealed record EditorSpan(
    int Start,
    int Length,
    EditorSpanFlags Flags,
    double SizePt = 0,
    string? FontName = null,
    string? LinkUrl = null,
    int ForeArgb = 0);

/// <summary>
/// Computes Markdown / task styling for the note editor as pure data. A port of
/// <c>EditorStyleEngine</c> in Sources/EditorStyleEngine.swift: the same
/// expressions, the same marker handling, the same completed-task dimming —
/// but instead of mutating <c>NSTextStorage</c> directly it emits
/// <see cref="EditorSpan"/>s that the RichEditBox host applies through the
/// Text Object Model.
///
/// Line separators may be <c>'\r'</c> or <c>'\n'</c> (RichEdit exposes its
/// paragraph marks as <c>'\r'</c>); every range is a position into that exact
/// string, and the expressions are matched against a <c>'\r'</c>→<c>'\n'</c>
/// translation of each fragment, which preserves offsets one-for-one.
/// </summary>
public static class EditorStyleEngine
{
    public const string MonoFontName = "Consolas";

    private static readonly Regex Heading =
        new(@"^(#{1,6})[ \t]+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Bold =
        new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    private static readonly Regex Italic =
        new(@"(?<![\*_])([\*_])(?=[^\*_\s])(.+?)(?<=[^\*_\s])\1(?![\*_])", RegexOptions.Compiled);
    private static readonly Regex Code =
        new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex Struck =
        new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
    private static readonly Regex Quote =
        new(@"^>[ \t]?(.*)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Bullet =
        new(@"^[ \t]*([-*+])[ \t]+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Link =
        new(@"\[([^\]\n]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>The only characters any expression can match on; one scan of
    /// the fragment skips the seven passes for ordinary text.</summary>
    private static readonly char[] MarkdownChars = "*_`~#>-+[".ToCharArray();
    private static readonly char[] LineBreaks = { '\r', '\n' };

    private static readonly HashSet<string> OpenableSchemes =
        new(StringComparer.Ordinal) { "http", "https", "mailto" };

    /// <summary>
    /// Style a slice of the note body. <paramref name="start"/> and
    /// <paramref name="length"/> pick the slice; returned spans carry absolute
    /// positions. Completed-task spans are emitted last so their dimming wins
    /// over the Markdown passes, matching the Swift ordering.
    /// </summary>
    public static IReadOnlyList<EditorSpan> Style(
        string text,
        int start,
        int length,
        int inkArgb,
        double baseSizePt,
        bool markdownEnabled,
        Func<string, bool> isCompletedTask)
    {
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + length, start, text.Length);
        var fragment = text.Substring(start, end - start);
        var spans = new List<EditorSpan>();

        if (markdownEnabled && fragment.IndexOfAny(MarkdownChars) >= 0)
            Markdown(spans, fragment, start, inkArgb, baseSizePt);

        StyleCompletedTasks(spans, fragment, start, inkArgb, isCompletedTask);
        return spans;
    }

    /// <summary>The Markdown links in a fragment as (label start, label length,
    /// openable URL or null). Used by the host for ⌘-click without keeping
    /// hot ranges in sync with edits.</summary>
    public static IReadOnlyList<(int Start, int Length, string? Url)> LinksIn(string fragment)
    {
        var result = new List<(int, int, string?)>();
        foreach (Match m in Link.Matches(fragment))
            result.Add((m.Groups[1].Index, m.Groups[1].Length, OpenableUrl(m.Groups[2].Value)));
        return result;
    }

    /// <summary>The destination of a Markdown link, or null when it is not one
    /// of the three openable schemes. A note is ordinary text — nothing typed
    /// or imported into it may become a launcher for anything else.</summary>
    public static string? OpenableUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not { } scheme)
            return null;
        return OpenableSchemes.Contains(scheme.ToLowerInvariant()) ? uri.AbsoluteUri : null;
    }

    /// <summary>Base direction of a paragraph from its first strong character,
    /// for <c>Automatic</c> direction. Neutral characters (digits, punctuation)
    /// never decide.</summary>
    public static bool FirstStrongIsRtl(string paragraph)
    {
        foreach (var c in paragraph)
        {
            if (IsRtlStrong(c)) return true;
            if (IsLtrStrong(c)) return false;
        }
        return false;
    }

    /// <summary>The complete line containing a location, terminator included.
    /// At EOF after a trailing newline this returns the zero-length final line.</summary>
    public static (int Start, int Length) LineRangeContaining(string text, int location)
    {
        var loc = Math.Clamp(location, 0, text.Length);
        var start = loc;
        while (start > 0 && !IsLineBreak(text[start - 1])) start--;
        var end = loc;
        while (end < text.Length && !IsLineBreak(text[end])) end++;
        if (end < text.Length) end++;
        return (start, end - start);
    }

    /// <summary>
    /// Expand character edits to complete lines. One character on either side
    /// is included before expansion so inserting or deleting a newline restyles
    /// both paragraphs that changed identity.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> AffectedLineRanges(
        string text, IReadOnlyList<(int Start, int Length)> edits)
    {
        if (edits.Count == 0) return Array.Empty<(int, int)>();
        if (text.Length == 0) return new[] { (0, 0) };

        var expanded = new List<(int Start, int Length)>();
        foreach (var (editStart, editLength) in edits)
        {
            var safeStart = Math.Clamp(editStart, 0, text.Length);
            var safeEnd = Math.Clamp(editStart + editLength, safeStart, text.Length);
            var lower = Math.Max(0, safeStart - 1);
            var upper = Math.Min(text.Length, safeEnd + 1);
            var (a, al) = LineRangeContaining(text, lower);
            var (b, bl) = LineRangeContaining(text, Math.Max(lower, upper));
            expanded.Add((a, b + bl - a));
        }
        return MergeRanges(expanded, text.Length);
    }

    /// <summary>Clamp, sort, and merge overlapping or adjacent ranges.
    /// Distant caret lines stay disjoint so styling never scans the gap.</summary>
    public static IReadOnlyList<(int Start, int Length)> MergeRanges(
        IReadOnlyList<(int Start, int Length)> ranges, int textLength, bool keepEmpty = false)
    {
        var safe = new List<(int Start, int Length)>();
        foreach (var (start, length) in ranges)
        {
            if (start < 0) continue;
            var s = Math.Min(start, textLength);
            var l = Math.Min(length, textLength - s);
            if (l <= 0 && !keepEmpty) continue;
            safe.Add((s, l));
        }
        safe.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.Length.CompareTo(y.Length));
        if (safe.Count == 0) return Array.Empty<(int, int)>();

        var result = new List<(int Start, int Length)> { safe[0] };
        for (var i = 1; i < safe.Count; i++)
        {
            var cur = result[^1];
            var next = safe[i];
            if (next.Start <= cur.Start + cur.Length)
            {
                var upper = Math.Max(cur.Start + cur.Length, next.Start + next.Length);
                result[^1] = (cur.Start, upper - cur.Start);
            }
            else
            {
                result.Add(next);
            }
        }
        return result;
    }

    public static int WithAlpha(int argb, double alpha)
    {
        var a = (int)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        return (a << 24) | (argb & 0x00FFFFFF);
    }

    // MARK: The seven Markdown passes (order matches the Swift engine)

    private static void Markdown(
        List<EditorSpan> spans, string fragment, int offset, int inkArgb, double baseSizePt)
    {
        var faint = WithAlpha(inkArgb, 0.32);
        void Dim(int index, int length)
        {
            if (length > 0)
                spans.Add(new EditorSpan(offset + index, length, EditorSpanFlags.None, ForeArgb: faint));
        }

        // Match against '\n'-separated text; '\r' → '\n' preserves offsets.
        var text = fragment.Contains('\r') ? fragment.Replace('\r', '\n') : fragment;

        foreach (Match m in Heading.Matches(text))
        {
            var level = m.Groups[1].Value.Length;
            var bump = Math.Max(1.5, 7 - level * 1.1);
            spans.Add(new EditorSpan(offset + m.Index, m.Length, EditorSpanFlags.Bold,
                SizePt: baseSizePt + bump));
            Dim(m.Groups[1].Index, m.Groups[1].Length);
        }
        foreach (Match m in Link.Matches(text))
        {
            var label = m.Groups[1];
            var url = OpenableUrl(m.Groups[2].Value);
            spans.Add(new EditorSpan(offset + label.Index, label.Length,
                EditorSpanFlags.Underline, LinkUrl: url));
            Dim(m.Index, 1);
            Dim(label.Index + label.Length, m.Index + m.Length - (label.Index + label.Length));
        }
        foreach (Match m in Bold.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Groups[2].Index, m.Groups[2].Length,
                EditorSpanFlags.Bold));
            Dim(m.Index, 2);
            Dim(m.Index + m.Length - 2, 2);
        }
        foreach (Match m in Italic.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Groups[2].Index, m.Groups[2].Length,
                EditorSpanFlags.Italic));
            Dim(m.Index, 1);
            Dim(m.Index + m.Length - 1, 1);
        }
        foreach (Match m in Code.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Groups[1].Index, m.Groups[1].Length,
                EditorSpanFlags.CodeBackground, FontName: MonoFontName));
            Dim(m.Index, 1);
            Dim(m.Index + m.Length - 1, 1);
        }
        foreach (Match m in Struck.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Groups[1].Index, m.Groups[1].Length,
                EditorSpanFlags.Strikethrough));
            Dim(m.Index, 2);
            Dim(m.Index + m.Length - 2, 2);
        }
        foreach (Match m in Quote.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Index, m.Length, EditorSpanFlags.None,
                ForeArgb: WithAlpha(inkArgb, 0.62)));
            spans.Add(new EditorSpan(offset + m.Groups[1].Index, m.Groups[1].Length,
                EditorSpanFlags.Italic, ForeArgb: WithAlpha(inkArgb, 0.62)));
            Dim(m.Index, 1);
        }
        foreach (Match m in Bullet.Matches(text))
        {
            spans.Add(new EditorSpan(offset + m.Groups[1].Index, m.Groups[1].Length,
                EditorSpanFlags.None, ForeArgb: WithAlpha(inkArgb, 0.5)));
        }
    }

    private static void StyleCompletedTasks(
        List<EditorSpan> spans, string fragment, int offset, int inkArgb,
        Func<string, bool> isCompletedTask)
    {
        var lineStart = 0;
        while (lineStart < fragment.Length)
        {
            var breakAt = fragment.IndexOfAny(LineBreaks, lineStart);
            var lineEnd = breakAt < 0 ? fragment.Length : breakAt;
            var line = fragment.Substring(lineStart, lineEnd - lineStart);
            if (isCompletedTask(line))
            {
                spans.Add(new EditorSpan(offset + lineStart, line.Length,
                    EditorSpanFlags.Strikethrough, ForeArgb: WithAlpha(inkArgb, 0.45)));
            }
            lineStart = lineEnd + 1;
        }
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n';

    private static bool IsRtlStrong(char c) =>
        (c >= '\u0590' && c <= '\u05FF') ||  // Hebrew
        (c >= '\u0600' && c <= '\u06FF') ||  // Arabic
        (c >= '\u0700' && c <= '\u074F') ||  // Syriac
        (c >= '\u0750' && c <= '\u077F') ||  // Arabic supplement
        (c >= '\u08A0' && c <= '\u08FF') ||  // Arabic extended-A
        (c >= '\uFB1D' && c <= '\uFDFF') ||  // Hebrew presentation forms
        (c >= '\uFE70' && c <= '\uFEFC');    // Arabic presentation forms

    private static bool IsLtrStrong(char c) =>
        char.IsLetter(c) && !IsRtlStrong(c);
}
