using System.Text.Json;
using NotyWin.App.Models;
using NotyWin.App.Geometry;

namespace NotyWin.Storage;

/// <summary>
/// JSON-on-disk settings store. The macOS app uses
/// <c>UserDefaults.standard</c>; the Win32 unpackaged equivalent is a single
/// file under <c>%LocalAppData%\Noty\settings.json</c>. Pure C#, no I/O
/// outside the given file path.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private SettingsSnapshot _current;

    public event Action<SettingsSnapshot>? Changed;

    public JsonSettingsStore(string path)
    {
        _path = path;
        _current = LoadFromDisk();
    }

    public SettingsSnapshot Load() => _current;

    public void Save(SettingsSnapshot snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Options));
        }
        Changed?.Invoke(snapshot);
    }

    private SettingsSnapshot LoadFromDisk()
    {
        if (!File.Exists(_path)) return new SettingsSnapshot();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SettingsSnapshot>(json) ?? new SettingsSnapshot();
        }
        catch
        {
            // Corrupt settings shouldn't take the app down — start clean.
            return new SettingsSnapshot();
        }
    }
}