using NotyWin.App.Models;
using NotyWin.Storage;
using Xunit;

namespace NotyWin.Storage.Tests;

public class SqliteNotePersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteNotePersistence _store;

    public SqliteNotePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NotyWinStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SqliteNotePersistence(
            Path.Combine(_dir, "notes.db"),
            Path.Combine(_dir, "note.key.dpapi"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Give Windows a moment to release file handles from the WAL.
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    [Fact]
    public void LoadAll_OnEmptyDb_ReturnsEmpty()
    {
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void Upsert_ThenLoad_RoundTripsBody()
    {
        var n = new Note { Id = "1", Title = "t", Body = "hello world", Color = 2, Created = DateTime.UtcNow, Modified = DateTime.UtcNow };
        _store.Upsert(n);
        var loaded = Assert.Single(_store.LoadAll());
        Assert.Equal("hello world", loaded.Body);
        Assert.Equal("t", loaded.Title);
        Assert.Equal(2, loaded.Color);
    }

    [Fact]
    public void Upsert_Existing_UpdatesInPlace()
    {
        var n = new Note { Id = "1", Body = "first" };
        _store.Upsert(n);
        _store.Upsert(new Note { Id = "1", Body = "second" });
        var loaded = Assert.Single(_store.LoadAll());
        Assert.Equal("second", loaded.Body);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        _store.Upsert(new Note { Id = "1", Body = "a" });
        _store.Upsert(new Note { Id = "2", Body = "b" });
        _store.Delete("1");
        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("2", loaded[0].Id);
    }

    [Fact]
    public void BodyIsEncryptedAtRest_BodyBlobNotPlaintext()
    {
        var plaintext = "this is a secret body line";
        _store.Upsert(new Note { Id = "1", Body = plaintext });
        var path = Path.Combine(_dir, "notes.db");
        // SQLite WAL keeps the file open; read with FileShare.ReadWrite.
        string s;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            s = sr.ReadToEnd();
        }
        Assert.DoesNotContain(plaintext, s);
    }

    [Fact]
    public void PinnedAndDirection_PreservedAcrossLoad()
    {
        _store.Upsert(new Note { Id = "1", Pinned = true, TextDirection = NoteTextDirection.RightToLeft });
        var loaded = _store.LoadAll();
        Assert.True(loaded[0].Pinned);
        Assert.Equal(NoteTextDirection.RightToLeft, loaded[0].TextDirection);
    }

    [Fact]
    public void DifferentKey_DoesNotDecrypt()
    {
        // Write with the existing store; create another store pointing at the
        // same DB but a *fresh* DPAPI-wrapped key. The first row should fail to
        // decrypt to the right plaintext (returns "").
        _store.Upsert(new Note { Id = "1", Body = "secret" });
        using var second = new SqliteNotePersistence(
            Path.Combine(_dir, "notes.db"),
            Path.Combine(_dir, "other.key.dpapi"));
        var loaded = second.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("", loaded[0].Body);
    }
}
