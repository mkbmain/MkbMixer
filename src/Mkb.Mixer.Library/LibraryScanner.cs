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

            foreach (string sub in SafeEnumerate(dir, directories: true))
            {
                string name = Path.GetFileName(sub);
                if (!SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    pending.Push(sub);
            }

            foreach (string file in SafeEnumerate(dir, directories: false))
            {
                ct.ThrowIfCancellationRequested();
                if (SupportedFormats.IsAudio(file))
                    yield return file;
            }

            // Yield to the scheduler so a scan of a large tree never monopolises
            // the thread pool thread it happens to be running on.
            await Task.Yield();
        }
    }

    /// <summary>Enumerates eagerly so an unreadable folder is skipped rather than aborting the scan.</summary>
    private static string[] SafeEnumerate(string dir, bool directories)
    {
        try
        {
            return directories
                ? Directory.GetDirectories(dir)
                : Directory.GetFiles(dir);
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (IOException) { return []; }
    }
}
