using NotyWin.App.Models;
using Xunit;

namespace NotyWin.Models.Tests;

public class NoteListTests
{
    [Fact]
    public void Create_AddsNote_PublishesToObservers()
    {
        var list = new NoteList(Array.Empty<Note>());
        var fired = 0;
        list.Subscribe(new CountingObserver(() => fired++));
        Assert.Equal(1, fired);   // Subscribe fires immediately
        list.Create("hello");
        Assert.Equal(2, fired);
        Assert.Single(list.Notes);
    }

    [Fact]
    public void Create_NewestSitsAtTopOfDeck()
    {
        var list = new NoteList(Array.Empty<Note>());
        var a = list.Create("a");
        var b = list.Create("b");
        Assert.True(b.Order < a.Order);
    }

    [Fact]
    public void UpdateBody_RewritesTitle_FromFirstLine()
    {
        var list = new NoteList(Array.Empty<Note>());
        var n = list.Create("First line\nbody");
        Assert.Equal("First line", n.Title);
        list.UpdateBody(n.Id, "Second line\nbody");
        Assert.Equal("Second line", list.ById(n.Id)!.Title);
    }

    [Fact]
    public void TogglePin_Flips()
    {
        var list = new NoteList(Array.Empty<Note>());
        var n = list.Create("x");
        Assert.False(n.Pinned);
        list.TogglePin(n.Id);
        Assert.True(list.ById(n.Id)!.Pinned);
    }

    [Fact]
    public void CycleColor_WrapsAtEight()
    {
        var list = new NoteList(Array.Empty<Note>());
        var n = list.Create("x");
        var start = n.Color;
        for (var i = 0; i < 8; i++) list.CycleColor(n.Id);
        Assert.Equal(start, list.ById(n.Id)!.Color);
    }

    [Fact]
    public void SetArchived_True_ExcludesFromActive()
    {
        var list = new NoteList(Array.Empty<Note>());
        var a = list.Create("a");
        var b = list.Create("b");
        list.SetArchived(a.Id, true);
        Assert.Single(list.Active);
        Assert.Equal(b.Id, list.Active.First().Id);
    }

    [Fact]
    public void Unarchive_PutsAtTopOfDeck()
    {
        var list = new NoteList(Array.Empty<Note>());
        var a = list.Create("a");
        var b = list.Create("b");
        list.SetArchived(a.Id, true);
        list.SetArchived(a.Id, false);
        var activeIds = list.Active.Select(n => n.Id).ToList();
        Assert.Equal(a.Id, activeIds[0]);   // back to the top
    }

    [Fact]
    public void Delete_SetsPendingUndo()
    {
        var list = new NoteList(Array.Empty<Note>());
        var n = list.Create("x");
        list.Delete(n.Id, TimeSpan.FromSeconds(10));
        Assert.Empty(list.Notes);
        Assert.NotNull(list.PendingUndo);
    }

    [Fact]
    public void UndoDelete_Restores()
    {
        var list = new NoteList(Array.Empty<Note>());
        var n = list.Create("x");
        list.Delete(n.Id, TimeSpan.FromSeconds(10));
        list.UndoDelete();
        Assert.Single(list.Notes);
        Assert.Equal(n.Id, list.Notes[0].Id);
        Assert.Null(list.PendingUndo);
    }

    [Fact]
    public void Reorder_MovesBySlots()
    {
        var list = new NoteList(Array.Empty<Note>());
        // Newest sits at the top (smallest order). c is top, a is bottom.
        var a = list.Create("a");
        var b = list.Create("b");
        var c = list.Create("c");
        list.Reorder(c.Id, +2);   // move top down 2 slots → bottom
        var ids = list.Active.Select(n => n.Id).ToList();
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, ids);
    }

    [Fact]
    public void Reorder_RewritesOrderDensely()
    {
        var list = new NoteList(Array.Empty<Note>());
        list.Create("a");
        list.Create("b");
        list.Create("c");
        // Move the top item (newest) down 2 slots → ends up at the bottom.
        var topId = list.Notes.OrderBy(n => n.Order).First().Id;
        list.Reorder(topId, 2);
        var orders = list.Notes.OrderBy(n => n.Order).Select(n => n.Order).ToList();
        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, orders);
    }

    [Fact]
    public void Ingest_AssignsFreshIdOnCollision()
    {
        var list = new NoteList(Array.Empty<Note>());
        var original = list.Create("x");
        list.Ingest(new[] { new Note { Id = original.Id, Body = "duplicate" } });
        Assert.Equal(2, list.Notes.Count);
        Assert.Contains(list.Notes, n => n.Id == original.Id);
        Assert.Contains(list.Notes, n => n.Id != original.Id);
    }

    [Fact]
    public void ActiveCount_ExcludesArchived()
    {
        var list = new NoteList(Array.Empty<Note>());
        list.Create("a");
        list.Create("b");
        list.SetArchived(list.Notes[0].Id, true);
        Assert.Equal(1, list.ActiveCount);
    }

    private sealed class CountingObserver : IObserver<NoteList>
    {
        private readonly Action _onNext;
        public CountingObserver(Action onNext) { _onNext = onNext; }
        public void OnNext(NoteList value) => _onNext();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
