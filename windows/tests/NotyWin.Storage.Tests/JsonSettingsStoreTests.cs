using NotyWin.Storage;
using Xunit;

namespace NotyWin.Storage.Tests;

public class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NotyWinSettingsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var s = new JsonSettingsStore(_path);
        var defaults = s.Load();
        Assert.True(defaults.TabPreview);
        Assert.Equal(1.0, defaults.DeckScale);
        Assert.Equal("all", defaults.DisplayTarget);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var s1 = new JsonSettingsStore(_path);
        var snapshot = s1.Load() with { DeckScale = 1.5, DeckOnLeftEdge = true, ShowOverFullScreen = true };
        s1.Save(snapshot);

        var s2 = new JsonSettingsStore(_path);
        var loaded = s2.Load();
        Assert.Equal(1.5, loaded.DeckScale);
        Assert.True(loaded.DeckOnLeftEdge);
        Assert.True(loaded.ShowOverFullScreen);
    }

    [Fact]
    public void Save_RaisesChangedEvent()
    {
        var s = new JsonSettingsStore(_path);
        var raised = 0;
        s.Changed += _ => raised++;
        s.Save(s.Load() with { DeckScale = 1.2 });
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ this is not json");
        var s = new JsonSettingsStore(_path);
        var defaults = s.Load();
        Assert.Equal(1.0, defaults.DeckScale);
    }
}
