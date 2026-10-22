using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>
/// One folder in the browser tree, expanded lazily.
/// </summary>
/// <remarks>
/// The original scanned an entire drive up front, on the UI thread, before showing
/// anything. Children are loaded here only when a node is actually expanded, and
/// off the UI thread: each one is probed for subfolders, which on an SD card with
/// hundreds of artist folders is a noticeable stall.
/// </remarks>
public sealed partial class FolderNode : ObservableObject
{
    private bool _loaded;

    public FolderNode(string path) : this(path, isPlaceholder: false, MightHaveChildren(path)) { }

    /// <summary>A root with a name the platform chose, such as "SanDisk SD card".</summary>
    public FolderNode(string path, string name) : this(path) => Name = name;

    /// <summary>The fixed "Recently played" entry at the top of the tree. It has no path or children.</summary>
    public static FolderNode RecentlyPlayed() => new();

    private FolderNode()
    {
        Path = string.Empty;
        Name = "★ Recently played";
        IsRecentlyPlayed = true;
    }

    public bool IsRecentlyPlayed { get; }

    private FolderNode(string path, bool isPlaceholder, bool mightHaveChildren = false)
    {
        Path = path;
        IsPlaceholder = isPlaceholder;
        Name = isPlaceholder ? path : FriendlyName(path);

        // A fresh instance per node: a TreeView cannot place one item under two
        // parents, which breaks container generation and selection.
        if (mightHaveChildren)
            Children.Add(new FolderNode("…", isPlaceholder: true));
    }

    /// <summary>Roots read better as "Home" and "Filesystem" than as "root" and "/".</summary>
    private static string FriendlyName(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (full == System.IO.Path.GetPathRoot(full))
            return OperatingSystem.IsWindows() ? full : "Filesystem";
        if (full == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            return "Home";
        return System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n
            ? n
            : full;
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>The first expansion's read of the children; null until expanded. Tests wait on it.</summary>
    public Task? ChildrenLoaded { get; private set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _loaded = true;
            ChildrenLoaded = LoadChildrenAsync();
        }
    }

    private static bool MightHaveChildren(string path)
    {
        try { return Directory.EnumerateDirectories(path).Any(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    private async Task LoadChildrenAsync()
    {
        // The placeholder stays until the real children are ready, so the expander
        // does not flicker shut while they are read.
        (string Path, bool MightHaveChildren)[] found = await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateDirectories(Path)
                    .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => (d, MightHaveChildren(d)))
                    .ToArray();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return [];   // an unreadable folder simply shows as empty
            }
        });

        // Back on the UI thread: the tree is bound to this collection.
        Children.Clear();
        foreach (var (dir, mightHaveChildren) in found)
            Children.Add(new FolderNode(dir, isPlaceholder: false, mightHaveChildren));
    }
}
