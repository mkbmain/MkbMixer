namespace Mkb.Mixer.Audio;

/// <summary>A playable audio file plus whatever metadata we could read from it.</summary>
public sealed record Track(
    string Path,
    string Title,
    string? Artist = null,
    string? Album = null,
    TimeSpan Duration = default)
{
    /// <summary>Falls back to the file name when a track has no ID3 title.</summary>
    public static Track FromPath(string path) =>
        new(path, System.IO.Path.GetFileNameWithoutExtension(path));

    public string Display => Artist is { Length: > 0 } ? $"{Artist} — {Title}" : Title;

    /// <summary>
    /// Duration as m:ss. Done here rather than with a XAML StringFormat, because
    /// TimeSpan's "m" specifier is the minutes *component* and silently produces
    /// nonsense like "44:00" for a 3m44s track once the hours roll over.
    /// </summary>
    public string DurationText => Duration <= TimeSpan.Zero
        ? "--:--"
        : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:00}";
}

public enum PlaybackState { Stopped, Playing, Paused }

/// <summary>Which of the two decks a thing belongs to.</summary>
public enum DeckId { A, B }
