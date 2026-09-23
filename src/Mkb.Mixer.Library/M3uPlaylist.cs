using System.Globalization;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>Reads and writes extended M3U playlists.</summary>
/// <remarks>
/// The original kept playlists only in the two ListBox controls, so closing the app
/// lost everything. M3U is the obvious interchange format: other players can open
/// what this app writes, and vice versa.
/// </remarks>
public static class M3uPlaylist
{
    private const string Header = "#EXTM3U";
    private const string InfoPrefix = "#EXTINF:";

    public static void Save(string path, IEnumerable<Track> tracks)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine(Header);
        foreach (Track t in tracks)
        {
            int seconds = t.Duration > TimeSpan.Zero ? (int)t.Duration.TotalSeconds : -1;
            string label = t.Artist is { Length: > 0 } ? $"{t.Artist} - {t.Title}" : t.Title;
            writer.WriteLine($"{InfoPrefix}{seconds},{label}");
            writer.WriteLine(t.Path);
        }
    }

    public static IEnumerable<Track> Load(string path)
    {
        if (!File.Exists(path))
            yield break;

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string? pendingArtist = null, pendingTitle = null;
        TimeSpan pendingDuration = default;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(InfoPrefix, StringComparison.Ordinal))
            {
                (pendingArtist, pendingTitle, pendingDuration) = ParseInfo(line);
                continue;
            }

            if (line[0] == '#')
                continue;

            string full = Path.IsPathRooted(line) ? line : Path.Combine(baseDir, line);
            yield return new Track(
                full,
                pendingTitle ?? Path.GetFileNameWithoutExtension(full),
                pendingArtist,
                Duration: pendingDuration);

            pendingArtist = pendingTitle = null;
            pendingDuration = default;
        }
    }

    /// <summary>Parses <c>#EXTINF:seconds,Artist - Title</c>.</summary>
    private static (string? Artist, string? Title, TimeSpan Duration) ParseInfo(string line)
    {
        string body = line[InfoPrefix.Length..];
        int comma = body.IndexOf(',');
        if (comma < 0)
            return (null, null, default);

        TimeSpan duration = default;
        if (int.TryParse(body[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs) && secs > 0)
            duration = TimeSpan.FromSeconds(secs);

        string label = body[(comma + 1)..].Trim();
        int dash = label.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0
            ? (label[..dash].Trim(), label[(dash + 3)..].Trim(), duration)
            : (null, label, duration);
    }
}
