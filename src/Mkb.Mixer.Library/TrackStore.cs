using System.Text.Json;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>
/// Per-track memory — analysis results, hot cues, BPM corrections and play
/// history — kept as one JSON file keyed by full path.
/// </summary>
/// <remarks>
/// Held in memory and written about two seconds after the last change, so a burst
/// of background analysis is one write, not hundreds. Writes go to a temp file
/// and are moved over the real one, so a crash mid-write cannot corrupt it. A
/// corrupt or unreadable file starts the store empty rather than stopping the app,
/// as <see cref="SettingsStore"/> does.
/// </remarks>
public sealed class TrackStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string? _path;
    private readonly TimeSpan _saveDelay;
    private readonly Dictionary<string, TrackInfo> _tracks;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Timer? _saveTimer;
    private bool _dirty;

    /// <param name="path">Where to save, or null to keep everything in memory.</param>
    public TrackStore(string? path, TimeSpan? saveDelay = null)
    {
        _path = path;
        _saveDelay = saveDelay ?? TimeSpan.FromSeconds(2);
        _tracks = Load(path);
        if (path is not null)
            _saveTimer = new Timer(_ => Flush());
    }

    public static TrackStore InMemory() => new(null);

    /// <summary>Beside settings.json in the per-user config directory.</summary>
    public static TrackStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MkbMixer", "tracks.json"));

    /// <summary>
    /// What is known about a track. If the file has changed since it was analysed,
    /// the analysis is left out but hot cues and history are kept.
    /// </summary>
    public TrackInfo? Get(string path)
    {
        TrackInfo? info;
        lock (_gate) _tracks.TryGetValue(path, out info);
        if (info is null || !info.IsAnalysed) return info;
        return info.Stamp == FileStamp.Of(path) ? info : info.WithoutAnalysis();
    }

    public void Update(string path, Func<TrackInfo, TrackInfo> change)
    {
        lock (_gate)
        {
            TrackInfo current = _tracks.TryGetValue(path, out TrackInfo? existing) ? existing : new TrackInfo();
            _tracks[path] = change(current);
            _dirty = true;
        }
        _saveTimer?.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    public void SetAnalysis(string path, TrackAnalysis analysis)
    {
        FileStamp stamp = FileStamp.Of(path);
        Update(path, i => i with
        {
            FileSize = stamp.Size,
            FileWriteUtc = stamp.WriteUtc,
            IsAnalysed = true,
            Bpm = analysis.Bpm,
            BeatOffsetSeconds = analysis.BeatOffset?.TotalSeconds,
            FirstSoundSeconds = analysis.FirstSound?.TotalSeconds,
            LastSoundSeconds = analysis.LastSound?.TotalSeconds
        });
    }

    public void MarkPlayed(string path, DateTime utcNow) =>
        Update(path, i => i with { LastPlayedUtc = utcNow, PlayCount = i.PlayCount + 1 });

    /// <summary>Paths of the most recently played tracks, newest first.</summary>
    public IReadOnlyList<string> RecentlyPlayed(int max)
    {
        lock (_gate)
            return _tracks
                .Where(kv => kv.Value.LastPlayedUtc is not null)
                .OrderByDescending(kv => kv.Value.LastPlayedUtc)
                .Take(max)
                .Select(kv => kv.Key)
                .ToList();
    }

    /// <summary>Writes now if anything has changed. Safe to call from any thread.</summary>
    public void Flush()
    {
        if (_path is null) return;
        lock (_writeGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_tracks, Options);
                _dirty = false;
            }
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing track memory is not worth crashing over; try again next change.
                lock (_gate) _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Flush();
    }

    private static Dictionary<string, TrackInfo> Load(string? path)
    {
        var empty = new Dictionary<string, TrackInfo>(PathComparer);
        if (path is null || !File.Exists(path)) return empty;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, TrackInfo>>(File.ReadAllText(path), Options);
            if (loaded is null) return empty;
            foreach (var (key, info) in loaded)
                empty[key] = info.HotCueSeconds?.Length == TrackInfo.HotCueCount
                    ? info
                    : info with { HotCueSeconds = Resize(info.HotCueSeconds) };
            return empty;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return empty;
        }

        static double?[] Resize(double?[]? cues)
        {
            var fixedSize = new double?[TrackInfo.HotCueCount];
            if (cues is not null) Array.Copy(cues, fixedSize, Math.Min(cues.Length, fixedSize.Length));
            return fixedSize;
        }
    }
}
