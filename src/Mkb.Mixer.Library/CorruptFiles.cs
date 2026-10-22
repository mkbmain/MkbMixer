namespace Mkb.Mixer.Library;

/// <summary>What went wrong reading a store file at startup.</summary>
/// <param name="FileName">The file's name, without its directory.</param>
/// <param name="MovedTo">
/// Where the unreadable file was kept, or null if it could not be moved. In that
/// case the store does not write for the rest of the session.
/// </param>
public sealed record LoadProblem(string FileName, string? MovedTo);

/// <summary>
/// Keeps an unreadable store file out of the way instead of letting the next save
/// overwrite it: it may be hand-edited, or only damaged in a way someone can fix.
/// </summary>
internal static class CorruptFiles
{
    /// <summary>
    /// Renames <paramref name="path"/> to <c>path.corrupt-yyyyMMdd-HHmmss</c>, adding
    /// <c>-2</c>, <c>-3</c> and so on if that name is taken, so a second bad file in
    /// the same second does not replace the first.
    /// </summary>
    /// <returns>The new path, or null if the file could not be renamed.</returns>
    public static string? SetAside(string path, DateTime now)
    {
        string baseName = $"{path}.corrupt-{now:yyyyMMdd-HHmmss}";
        try
        {
            string target = baseName;
            for (int n = 2; File.Exists(target) || Directory.Exists(target); n++)
                target = $"{baseName}-{n}";
            File.Move(path, target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
