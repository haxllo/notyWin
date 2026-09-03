namespace NotyWin.App.Models;

/// <summary>
/// Checkbox tasks. Stored inline in the note body as <c>☐</c>/<c>☑</c> line
/// prefixes so a note stays plain text; Markdown import/export maps to and from
/// <c>- [ ]</c>/<c>- [x]</c>. Identical semantics to <c>Tasks</c> in
/// Sources/Core.swift.
/// </summary>
public static class Tasks
{
    public const char Open = '\u2610';
    public const char Done = '\u2611';
    public const string OpenPrefix = "\u2610 ";
    public const string DonePrefix = "\u2611 ";

    public static char? MarkerOf(string line)
    {
        if (line.Length == 0) return null;
        var f = line[0];
        return f == Open || f == Done ? f : null;
    }

    public static bool IsTask(string line) => MarkerOf(line) != null;

    public static string Stripped(string line)
    {
        if (!IsTask(line)) return line;
        return line.Substring(1).TrimStart();
    }

    public static (int Done, int Total) Progress(string body)
    {
        var done = 0;
        var total = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            switch (MarkerOf(line.TrimStart()))
            {
                case Done: done++; total++; break;
                case Open: total++; break;
            }
        }
        return (done, total);
    }
}