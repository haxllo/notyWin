namespace NotyWin.App.Models;

public sealed class PendingDelete
{
    public required Note Note { get; init; }
    public required DateTime Deadline { get; init; }
}