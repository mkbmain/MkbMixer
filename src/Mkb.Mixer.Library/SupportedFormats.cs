namespace Mkb.Mixer.Library;

/// <summary>The audio extensions the app will offer to load.</summary>
/// <remarks>
/// The original accepted .mp3, .wma and .wav. Those are all still here, but .wma
/// only decodes on Windows — it is a Microsoft codec and the cross-platform
/// decoders do not cover it. In exchange we gain FLAC, OGG and M4A everywhere.
/// </remarks>
public static class SupportedFormats
{
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma"
        };

    /// <summary>Extensions that only decode on Windows.</summary>
    public static readonly IReadOnlySet<string> WindowsOnlyExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wma" };

    public static bool IsAudio(string path) =>
        Extensions.Contains(Path.GetExtension(path));

    public static bool IsWindowsOnly(string path) =>
        WindowsOnlyExtensions.Contains(Path.GetExtension(path));
}
