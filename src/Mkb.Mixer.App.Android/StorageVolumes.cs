using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.OS.Storage;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// Every mounted storage volume, as browser roots: internal shared storage, SD
/// cards and USB drives.
/// </summary>
/// <remarks>
/// Scoped storage still exposes a removable volume's audio files by path to an app
/// holding the audio permission, so these work with the same file APIs the
/// rest of the app uses. The system folder picker would hand back content URIs
/// instead, which nothing downstream (tag reading, decoding, M3U) understands.
/// </remarks>
internal static class StorageVolumes
{
    public static IEnumerable<StorageRoot> List(Context context)
    {
        if (context.GetSystemService(Context.StorageService) is not StorageManager storage)
            yield break;

        foreach (StorageVolume volume in storage.StorageVolumes)
        {
            if (volume.State is not (global::Android.OS.Environment.MediaMounted
                                     or global::Android.OS.Environment.MediaMountedReadOnly))
                continue;
            if (PathOf(context, volume) is not { } path)
                continue;

            string name = volume.GetDescription(context) is { Length: > 0 } d ? d : Path.GetFileName(path);

            // Most music on the primary volume lives under Music, so offer that
            // first, as the browser always has.
            if (volume.IsPrimary && Path.Combine(path, "Music") is var music && Directory.Exists(music))
                yield return new StorageRoot(music, "Music");
            yield return new StorageRoot(path, name);
        }
    }

    private static string? PathOf(Context context, StorageVolume volume)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            return volume.Directory?.AbsolutePath;

        // Before Android 11 a volume does not expose its path. Each volume's
        // app-specific directory does, though: /storage/XXXX-XXXX/Android/data/<pkg>/files.
        foreach (Java.IO.File? dir in context.GetExternalFilesDirs(null) ?? [])
        {
            if (dir is null || context.GetSystemService(Context.StorageService) is not StorageManager storage)
                continue;
            if (!Equals(storage.GetStorageVolume(dir), volume))
                continue;
            int cut = dir.AbsolutePath.IndexOf("/Android/", StringComparison.Ordinal);
            return cut > 0 ? dir.AbsolutePath[..cut] : null;
        }
        return null;
    }
}
