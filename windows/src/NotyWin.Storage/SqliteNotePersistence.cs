using Microsoft.Data.Sqlite;
using NotyWin.App.Models;

namespace NotyWin.Storage;

/// <summary>
/// SQLite-backed note storage. Bodies are AES-GCM sealed; title / colour /
/// dates / pinned / direction stay plaintext so lists can render without
/// unsealing every row. Mirrors <c>Store</c> in Sources/Store.swift.
/// </summary>
public sealed class SqliteNotePersistence : INotePersistence, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly byte[] _key;

    public SqliteNotePersistence(string dbPath, string wrappedKeyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _key = NoteCipher.LoadOrCreateKey(wrappedKeyPath);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        ApplyPragmas();
        EnsureSchema();
    }

    private void ApplyPragmas()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS notes (
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL DEFAULT '',
              body BLOB NOT NULL,
              color INTEGER NOT NULL DEFAULT 0,
              created REAL NOT NULL,
              modified REAL NOT NULL,
              archived INTEGER NOT NULL DEFAULT 0,
              sort_order REAL NOT NULL DEFAULT 0,
              pinned INTEGER NOT NULL DEFAULT 0,
              text_direction TEXT NOT NULL DEFAULT 'automatic'
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_notes_archived ON notes(archived, sort_order);");
        Migrate();
    }

    private void Migrate()
    {
        var existing = new HashSet<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(notes);";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1));
        }
        if (!existing.Contains("pinned"))
            Exec("ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;");
        if (!existing.Contains("text_direction"))
            Exec("ALTER TABLE notes ADD COLUMN text_direction TEXT NOT NULL DEFAULT 'automatic';");
    }

    public IReadOnlyList<Note> LoadAll()
    {
        var out_ = new List<Note>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,title,body,color,created,modified,archived,sort_order,pinned,text_direction FROM notes ORDER BY sort_order ASC;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            out_.Add(new Note
            {
                Id = r.GetString(0),
                Title = r.IsDBNull(1) ? "" : r.GetString(1),
                Body = NoteCipher.Open(GetBytes(r, 2), _key),
                Color = r.GetInt32(3),
                Created = FromUnix(r.GetDouble(4)),
                Modified = FromUnix(r.GetDouble(5)),
                Archived = r.GetInt32(6) != 0,
                Order = r.GetDouble(7),
                Pinned = r.GetInt32(8) != 0,
                TextDirection = NoteTextDirectionExtensions.FromWire(r.IsDBNull(9) ? null : r.GetString(9)),
            });
            // Re-derive title if the persisted one is empty but body has content.
            var last = out_[^1];
            if (string.IsNullOrEmpty(last.Title) && !string.IsNullOrEmpty(last.Body))
                last.Title = Note.DerivedTitle(last.Body);
        }
        return out_;
    }

    public void Upsert(Note n)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (id,title,body,color,created,modified,archived,sort_order,pinned,text_direction)
            VALUES ($id,$title,$body,$color,$created,$modified,$archived,$sort,$pinned,$direction)
            ON CONFLICT(id) DO UPDATE SET
              title=excluded.title, body=excluded.body, color=excluded.color,
              modified=excluded.modified, archived=excluded.archived,
              sort_order=excluded.sort_order, pinned=excluded.pinned,
              text_direction=excluded.text_direction;
            """;
        cmd.Parameters.AddWithValue("$id", n.Id);
        cmd.Parameters.AddWithValue("$title", n.Title ?? "");
        cmd.Parameters.AddWithValue("$body", NoteCipher.Seal(n.Body ?? "", _key));
        cmd.Parameters.AddWithValue("$color", n.Color);
        cmd.Parameters.AddWithValue("$created", ToUnix(n.Created));
        cmd.Parameters.AddWithValue("$modified", ToUnix(n.Modified));
        cmd.Parameters.AddWithValue("$archived", n.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", n.Order);
        cmd.Parameters.AddWithValue("$pinned", n.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$direction", n.TextDirection.ToWire());
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static byte[] GetBytes(SqliteDataReader r, int i)
    {
        var len = r.GetBytes(i, 0, null, 0, 0);
        if (len == 0) return Array.Empty<byte>();
        var buf = new byte[len];
        r.GetBytes(i, 0, buf, 0, (int)len);
        return buf;
    }

    private static double ToUnix(DateTime utc) =>
        (utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static DateTime FromUnix(double s) =>
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
}