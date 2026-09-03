namespace NotyWin.App.Models;

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Color { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool Archived { get; set; }
    public bool Pinned { get; set; }
    public NoteTextDirection TextDirection { get; set; } = NoteTextDirection.Automatic;
    public double Order { get; set; }

    public NoteColor Palette => NoteColor.At(Color);

    /// <summary>Title shown in the fan / lists, derived from the first non-empty line.</summary>
    public static string DerivedTitle(string body)
    {
        var line = body.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? "";
        var clean = System.Text.RegularExpressions.Regex.Replace(line, @"^#{1,6}\s*", "");
        clean = Tasks.Stripped(clean).Trim();
        if (clean.Length == 0) return "";
        return clean.Length > 60 ? clean[..60] + "…" : clean;
    }

    public string DisplayTitle(string untitledLabel) => string.IsNullOrEmpty(Title) ? untitledLabel : Title;
}