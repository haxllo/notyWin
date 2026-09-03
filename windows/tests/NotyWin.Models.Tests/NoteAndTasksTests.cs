using NotyWin.App.Models;
using Xunit;

namespace NotyWin.Models.Tests;

public class TasksTests
{
    [Fact]
    public void MarkerOf_Open()
    {
        Assert.Equal(Tasks.Open, Tasks.MarkerOf("\u2610 first task"));
    }

    [Fact]
    public void MarkerOf_Done()
    {
        Assert.Equal(Tasks.Done, Tasks.MarkerOf("\u2611 done task"));
    }

    [Fact]
    public void MarkerOf_PlainLine()
    {
        Assert.Null(Tasks.MarkerOf("hello world"));
    }

    [Fact]
    public void Stripped_Open()
    {
        Assert.Equal("first task", Tasks.Stripped("\u2610 first task"));
    }

    [Fact]
    public void Stripped_PlainLine_Untouched()
    {
        Assert.Equal("hello", Tasks.Stripped("hello"));
    }

    [Fact]
    public void Progress_CountsDoneAndTotal()
    {
        var body = "\u2610 one\n\u2611 two\nplain\n\u2610 three";
        var (done, total) = Tasks.Progress(body);
        Assert.Equal(1, done);
        Assert.Equal(3, total);
    }

    [Fact]
    public void Progress_NoTasks_ReturnsZero()
    {
        var (done, total) = Tasks.Progress("just a note\nwith lines");
        Assert.Equal(0, done);
        Assert.Equal(0, total);
    }
}

public class NoteTitleTests
{
    [Fact]
    public void DerivedTitle_FirstLineTrimmed()
    {
        Assert.Equal("Hello world", Note.DerivedTitle("Hello world\nmore"));
    }

    [Fact]
    public void DerivedTitle_StripsHeadingMarker()
    {
        Assert.Equal("Heading", Note.DerivedTitle("## Heading"));
    }

    [Fact]
    public void DerivedTitle_StripsTaskMarker()
    {
        Assert.Equal("first task", Note.DerivedTitle("\u2610 first task"));
    }

    [Fact]
    public void DerivedTitle_TruncatesLong()
    {
        var body = new string('x', 100);
        var title = Note.DerivedTitle(body);
        Assert.Equal(61, title.Length);   // 60 + "…"
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void DerivedTitle_Empty()
    {
        Assert.Equal("", Note.DerivedTitle(""));
    }
}

public class NoteColorTests
{
    [Fact]
    public void All_HasEightColors()
    {
        Assert.Equal(8, NoteColor.All.Length);
    }

    [Fact]
    public void At_WrapsAround()
    {
        Assert.Equal(NoteColor.All[0], NoteColor.At(8));
        Assert.Equal(NoteColor.All[7], NoteColor.At(-1));
    }
}
