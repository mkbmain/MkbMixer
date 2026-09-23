using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>Reads ID3/Vorbis/MP4 tags, falling back to the file name.</summary>
/// <remarks>
/// The original showed raw paths like <c>C:\Music\track.mp3</c> in both playlists
/// and the browser. Reading tags is cheap and makes the lists legible.
/// </remarks>
public static class TrackMetadataReader
{
    public static Track Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            string? title = Blank(file.Tag.Title) ? null : file.Tag.Title;
            string? artist = Blank(file.Tag.FirstPerformer) ? null : file.Tag.FirstPerformer;
            string? album = Blank(file.Tag.Album) ? null : file.Tag.Album;

            return new Track(
                path,
                title ?? Path.GetFileNameWithoutExtension(path),
                artist,
                album,
                file.Properties?.Duration ?? TimeSpan.Zero);
        }
        catch (Exception e) when (e is TagLib.CorruptFileException
                                   or TagLib.UnsupportedFormatException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            // An unreadable tag is not a reason to hide the file from the user.
            return Track.FromPath(path);
        }
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
