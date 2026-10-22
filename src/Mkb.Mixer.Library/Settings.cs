using System.Text.Json;
using System.Text.Json.Serialization;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>Everything the app remembers between runs.</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public int CrossfadeSeconds { get; set; } = 20;
    public bool AutoCueEnabled { get; set; }
    public TempoMatchMode AutoCueTempoMatch { get; set; }
    public float CrossfaderPosition { get; set; } = 0.5f;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool DeckAShuffle { get; set; }
    public bool DeckARepeat { get; set; }
    public bool DeckBShuffle { get; set; }
    public bool DeckBRepeat { get; set; }
    public CueMode CueMode { get; set; } = CueMode.Off;
    public string? CueDevice { get; set; }
    public float CueMix { get; set; }

    /// <summary>Analyse library folders for BPM in the background. Null: on, except on phones.</summary>
    public bool? AnalyseLibraryBpm { get; set; }

    /// <summary>
    /// Keys the user changed, by action id. Text rather than a key type so this project
    /// needs no UI reference. A missing id means the default; an empty string means unbound.
    /// </summary>
    [JsonConverter(typeof(LenientStringDictionaryConverter))]
    public Dictionary<string, string>? Shortcuts { get; set; }

    /// <summary>
    /// Pulls a hand-edited or old file back into range, so nothing downstream sees a
    /// fade of 0 seconds, a fader past its end or an enum value that does not exist.
    /// </summary>
    internal AppSettings Normalised()
    {
        var defaults = new AppSettings();
        CrossfadeSeconds = Math.Max(1, CrossfadeSeconds);
        CrossfaderPosition = float.IsNaN(CrossfaderPosition) ? defaults.CrossfaderPosition : Math.Clamp(CrossfaderPosition, 0, 1);
        CueMix = float.IsNaN(CueMix) ? defaults.CueMix : Math.Clamp(CueMix, 0, 1);
        if (!Enum.IsDefined(CueMode)) CueMode = defaults.CueMode;
        if (!Enum.IsDefined(AutoCueTempoMatch)) AutoCueTempoMatch = defaults.AutoCueTempoMatch;
        if (!double.IsFinite(WindowWidth) || WindowWidth <= 0) WindowWidth = defaults.WindowWidth;
        if (!double.IsFinite(WindowHeight) || WindowHeight <= 0) WindowHeight = defaults.WindowHeight;
        return this;
    }
}

/// <summary>Persists <see cref="AppSettings"/> as JSON, defaulting rather than throwing.</summary>
public sealed class SettingsStore
{
    // Enums by name, so reordering one cannot remap a saved choice; numbers from
    // older files still read. Named floats, so a NaN never makes Save throw.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly string _path;
    private bool _writesDisabled;

    public SettingsStore(string path) => _path = path;

    /// <summary>Uses the per-user config directory the host platform expects.</summary>
    public static SettingsStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MkbMixer", "settings.json"));

    /// <summary>Set by <see cref="Load"/> if the file could not be read; null otherwise.</summary>
    public LoadProblem? LoadProblem { get; private set; }

    public AppSettings Load()
    {
        LoadProblem = null;
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
                    ?? new AppSettings()).Normalised();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file should never stop the app starting,
            // but it is kept, not overwritten. If it cannot be moved, Save stays off.
            string? movedTo = CorruptFiles.SetAside(_path, DateTime.Now);
            _writesDisabled = movedTo is null;
            LoadProblem = new LoadProblem(Path.GetFileName(_path), movedTo);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        if (_writesDisabled) return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Written beside and moved over, as TrackStore does: Android saves on
            // every pause, and a kill mid-write would otherwise truncate the file
            // and silently reset every setting on the next launch.
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing settings is not worth crashing over.
        }
    }
}

/// <summary>
/// Reads a string-to-string map without throwing on a hand-edited value of the wrong
/// type: a non-string value reads as null, and a non-object reads as no map at all.
/// Otherwise one bad shortcut would make the whole settings file unreadable.
/// </summary>
internal sealed class LenientStringDictionaryConverter : JsonConverter<Dictionary<string, string>?>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        var result = new Dictionary<string, string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) result[key] = reader.GetString()!;
            else
            {
                reader.Skip();
                result[key] = null!; // ShortcutMap treats null as "use the default"
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value);
}
