namespace NotyWin.App.Deck;

/// <summary>
/// Deck diagnostics. Off unless NOTY_DEBUG_DECK=1, matching the macOS app's
/// stderr trace switch — the paint path must never touch the disk in a
/// shipped build.
/// </summary>
internal static class DeckLog
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("NOTY_DEBUG_DECK") == "1";

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Noty", "deck.log");

    private static readonly object Gate = new();

    public static void Write(string tag, string msg)
    {
        if (!Enabled) return;
        lock (Gate)
            System.IO.File.AppendAllText(Path, $"[{DateTime.UtcNow:O}] {tag}: {msg}\n");
    }
}
