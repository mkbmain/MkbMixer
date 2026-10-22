namespace Mkb.Mixer.Library;

/// <summary>Walks a folder tree looking for audio files.</summary>
/// <remarks>
/// The original scanned recursively on the UI thread and called
/// <c>Application.DoEvents()</c> inside the loop to keep the window painting, which
/// froze the app on large drives and reentered event handlers unpredictably. This
/// streams results asynchronously and honours a cancellation token instead.
/// </remarks>
public static class LibraryScanner
{
    private static readonly string[] SkipDirectories =
        ["$recycle.bin", "system volume information", ".git", "node_modules"];

    // Links are not followed: one pointing back up the tree, like /proc/self/root
    // on Linux, would list everything below it over and over.
    private static readonly EnumerationOptions NoLinks = new() { AttributesToSkip = FileAttributes.ReparsePoint };

    public static async IAsyncEnumerable<string> ScanAsync(
        string root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            // Listed on the thread pool: a caller on the UI thread would otherwise
            // wait on every directory read, which on an SD card or "/" is noticeable.
            (string[] subs, string[] files) = await Task.Run(
                () => (SafeEnumerate(dir, directories: true), SafeEnumerate(dir, directories: false)), ct);

            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (!SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    pending.Push(sub);
            }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (SupportedFormats.IsAudio(file))
                    yield return file;
            }
        }
    }

    /// <summary>Enumerates eagerly so an unreadable folder is skipped rather than aborting the scan.</summary>
    private static string[] SafeEnumerate(string dir, bool directories)
    {
        try
        {
            return directories
                ? Directory.GetDirectories(dir, "*", NoLinks)
                : Directory.GetFiles(dir);
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (IOException) { return []; }
    }
}
