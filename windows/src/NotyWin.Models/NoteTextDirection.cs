namespace NotyWin.App.Models;

public enum NoteTextDirection
{
    Automatic,
    LeftToRight,
    RightToLeft,
}

public static class NoteTextDirectionExtensions
{
    public static string ToWire(this NoteTextDirection d) => d switch
    {
        NoteTextDirection.Automatic => "automatic",
        NoteTextDirection.LeftToRight => "leftToRight",
        NoteTextDirection.RightToLeft => "rightToLeft",
        _ => "automatic",
    };

    public static NoteTextDirection FromWire(string? raw) => raw switch
    {
        "leftToRight" => NoteTextDirection.LeftToRight,
        "rightToLeft" => NoteTextDirection.RightToLeft,
        _ => NoteTextDirection.Automatic,
    };
}