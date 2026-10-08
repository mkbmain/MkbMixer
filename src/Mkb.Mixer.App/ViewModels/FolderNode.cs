using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>
/// One folder in the browser tree, expanded lazily.
/// </summary>
/// <remarks>
/// The original scanned an entire drive up front, on the UI thread, before showing
/// anything. Children are loaded here only when a node is actually expanded.
/// </remarks>
public sealed partial class FolderNode : ObservableObject
{
    private bool _loaded;

    public FolderNode(string path) : this(path, isPlaceholder: false) { }

    /// <summary>A root with a name the platform chose, such as "SanDisk SD card".</summary>
    public FolderNode(string path, string name) : this(path, isPlaceholder: false) => Name = name;

    /// <summary>The fixed "Recently played" entry at the top of the tree. It has no path or children.</summary>
    public static FolderNode RecentlyPlayed() => new();

    private FolderNode()
    {
        Path = string.Empty;
        Name = "★ Recently played";
        IsRecentlyPlayed = true;
    }

    public bool IsRecentlyPlayed { get; }

    private FolderNode(string path, bool isPlaceholder)
    {
        Path = path;
        IsPlaceholder = isPlaceholder;
        Name = isPlaceholder ? path : FriendlyName(path);

        // A fresh instance per node: a TreeView cannot place one item under two
        // parents, which breaks container generation and selection.
        if (!isPlaceholder && MightHaveChildren())
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

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) LoadChildren();
    }

    private bool MightHaveChildren()
    {
        try { return Directory.EnumerateDirectories(Path).Any(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    private void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Path)
                         .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FolderNode(dir));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // An unreadable folder simply shows as empty.
        }
    }
}
