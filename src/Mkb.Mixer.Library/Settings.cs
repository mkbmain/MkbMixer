using System.Text.Json;

namespace Mkb.Mixer.Library;

/// <summary>Everything the app remembers between runs.</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public int CrossfadeSeconds { get; set; } = 20;
    public bool AutoCueEnabled { get; set; }
    public float CrossfaderPosition { get; set; } = 0.5f;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
}

/// <summary>Persists <see cref="AppSettings"/> as JSON, defaulting rather than throwing.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>Uses the per-user config directory the host platform expects.</summary>
    public static SettingsStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MkbMixer", "settings.json"));

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
                   ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file should never stop the app starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing settings is not worth crashing over.
        }
    }
}
