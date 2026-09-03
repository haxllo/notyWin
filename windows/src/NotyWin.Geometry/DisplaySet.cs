namespace NotyWin.App.Geometry;

/// <summary>
/// What displays should host a deck: all, the main one, or a pinned display id.
/// Port of <c>DeckManager.targetDisplayIDs()</c>.
/// </summary>
public enum DisplayTargetKind
{
    All,
    Main,
    Pinned,
}

/// <summary>Parsed view of the <c>Settings.displayTarget</c> string.</summary>
public sealed class DisplayTarget
{
    public DisplayTargetKind Kind { get; }
    public uint PinnedId { get; }

    public DisplayTarget(DisplayTargetKind kind, uint pinnedId = 0)
    {
        Kind = kind;
        PinnedId = pinnedId;
    }

    public static DisplayTarget Parse(string raw)
    {
        if (raw == "all") return new DisplayTarget(DisplayTargetKind.All);
        if (raw == "main") return new DisplayTarget(DisplayTargetKind.Main);
        if (raw.StartsWith("id:") && uint.TryParse(raw.AsSpan(3), out var id))
            return new DisplayTarget(DisplayTargetKind.Pinned, id);
        // Unknown — fall through to All (mirrors Swift default).
        return new DisplayTarget(DisplayTargetKind.All);
    }
}

/// <summary>Resolves which display ids should hold a deck given the target + available displays.</summary>
public static class DisplaySetResolver
{
    public static HashSet<uint> Resolve(
        DisplayTarget target,
        IReadOnlyDictionary<uint, DisplayRect> displays,
        uint mainId)
    {
        if (displays.Count == 0) return new HashSet<uint>();

        return target.Kind switch
        {
            DisplayTargetKind.All => new HashSet<uint>(displays.Keys),
            DisplayTargetKind.Main => new HashSet<uint> { mainId },
            DisplayTargetKind.Pinned when displays.ContainsKey(target.PinnedId)
                => new HashSet<uint> { target.PinnedId },
            DisplayTargetKind.Pinned
                => new HashSet<uint> { mainId },   // pinned display gone: fallback
            _ => new HashSet<uint>(displays.Keys),
        };
    }
}