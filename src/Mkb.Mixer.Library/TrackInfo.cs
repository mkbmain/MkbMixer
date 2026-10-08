using System.Text.Json.Serialization;

namespace Mkb.Mixer.Library;

/// <summary>A file's size and modified time, so a changed file is re-analysed rather than trusted.</summary>
public readonly record struct FileStamp(long Size, DateTime WriteUtc)
{
    public static FileStamp Missing { get; } = new(-1, DateTime.MinValue);

    public static FileStamp Of(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return Missing;
        }
    }
}

/// <summary>What the app remembers about one track between runs.</summary>
public sealed record TrackInfo
{
    public const int HotCueCount = 4;

    public long FileSize { get; init; } = -1;
    public DateTime FileWriteUtc { get; init; }

    /// <summary>True once analysed, even if no BPM was found, so the track is not analysed again.</summary>
    public bool IsAnalysed { get; init; }
    public double? Bpm { get; init; }
    public double? BeatOffsetSeconds { get; init; }
    public double? FirstSoundSeconds { get; init; }
    public double? LastSoundSeconds { get; init; }

    /// <summary>The user's x1/2 or x2 correction. Kept when the track is re-analysed.</summary>
    public double BpmMultiplier { get; init; } = 1;

    /// <summary>One entry per slot, null when the slot is empty. 0 is a real cue at the very start.</summary>
    public double?[] HotCueSeconds { get; init; } = new double?[HotCueCount];

    public DateTime? LastPlayedUtc { get; init; }
    public int PlayCount { get; init; }

    [JsonIgnore] public double? DisplayBpm => Bpm * BpmMultiplier;
    [JsonIgnore] public FileStamp Stamp => new(FileSize, FileWriteUtc);

    public TrackInfo WithoutAnalysis() => this with
    {
        IsAnalysed = false, Bpm = null, BeatOffsetSeconds = null,
        FirstSoundSeconds = null, LastSoundSeconds = null
    };

    public TrackInfo WithHotCue(int slot, double? seconds)
    {
        var cues = (double?[])HotCueSeconds.Clone();
        cues[slot] = seconds;
        return this with { HotCueSeconds = cues };
    }
}
