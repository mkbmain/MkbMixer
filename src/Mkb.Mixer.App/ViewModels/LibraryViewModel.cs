using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.App.Services;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>The library browser: folder tree, track list, search, BPM filter and play history.</summary>
public sealed partial class LibraryViewModel : ViewModelBase, IDisposable
{
    private readonly TrackStore _tracks;
    private readonly AnalysisQueue _analysis;
    private readonly Func<double?> _audibleBpm;
    private readonly Func<string, Track> _readTrack;
    private CancellationTokenSource? _scan;

    /// <param name="audibleBpm">The BPM the room is hearing, for the ≈ button.</param>
    /// <param name="lastFolder">Where the browser opens; the user's music folder when null.</param>
    /// <param name="readTrack">Reads a file's tags. Injectable so tests can hold a read open.</param>
    public LibraryViewModel(TrackStore tracks, AnalysisQueue analysis, Func<double?> audibleBpm,
                            string? lastFolder = null, bool analyseBpm = false,
                            Func<string, Track>? readTrack = null)
    {
        _readTrack = readTrack ?? TrackMetadataReader.Read;
        _tracks = tracks;
        _analysis = analysis;
        _audibleBpm = audibleBpm;
        _analyseLibraryBpm = analyseBpm;
        _currentFolder = lastFolder ?? DefaultFolder();
        analysis.Analysed += (_, path) => RefreshBpm(path);
    }

    /// <summary>Raised with a line for the status bar.</summary>
    public event EventHandler<string>? Status;

    private void Report(string message) => Status?.Invoke(this, message);

    /// <summary>Folders in the left-hand browser tree.</summary>
    public ObservableCollection<FolderNode> Roots { get; } = [];

    /// <summary>Audio files found in the selected folder, filtered by the search box.</summary>
    public ObservableCollection<LibraryRow> BrowserRows { get; } = [];

    [ObservableProperty] private FolderNode? _selectedFolder;
    [ObservableProperty] private LibraryRow? _selectedBrowserRow;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _currentFolder;
    [ObservableProperty] private bool _isScanning;

    /// <summary>
    /// Replaces the default browser roots when the platform knows better. Android
    /// supplies its mounted storage volumes here, SD cards and USB drives included.
    /// </summary>
    public Func<IEnumerable<StorageRoot>>? PlatformRoots { get; set; }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        if (value is { IsRecentlyPlayed: true })
            _ = ShowRecentlyPlayedAsync();
        // The "…" placeholder stands in for unexpanded children and is not a real
        // path; browsing it would fail with "could not find a part of the path".
        else if (value is { IsPlaceholder: false })
            _ = LoadFolderAsync(value.Path);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Shared storage on Android: what the user sees as "Internal storage". The
    /// .NET special folders point into the app's private sandbox there, which never
    /// holds any music.
    /// </summary>
    private const string AndroidStorage = "/storage/emulated/0";

    private static string DefaultFolder()
    {
        if (OperatingSystem.IsAndroid())
            return Path.Combine(AndroidStorage, "Music") is var music && Directory.Exists(music)
                ? music
                : AndroidStorage;

        return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } m && Directory.Exists(m)
            ? m
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Populates the browser roots. The original enumerated Windows drive letters,
    /// which has no meaning on Linux or macOS; this uses the user's real folders.
    /// </summary>
    public void LoadRoots()
    {
        Roots.Clear();
        Roots.Add(FolderNode.RecentlyPlayed());
        if (PlatformRoots is not null)
        {
            foreach (StorageRoot root in PlatformRoots().Where(r => Directory.Exists(r.Path)).DistinctBy(r => r.Path))
                Roots.Add(new FolderNode(root.Path, root.Name));
            return;
        }
        foreach (string path in CandidateRoots().Where(Directory.Exists).Distinct())
            Roots.Add(new FolderNode(path));
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return DefaultFolder();
        if (OperatingSystem.IsAndroid())
        {
            // "/" and the app's own home directory are useless to browse on Android.
            yield return AndroidStorage;
            yield break;
        }
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                yield return d.RootDirectory.FullName;
        else
            yield return "/";
    }

    [RelayCommand]
    private async Task BrowseFolderAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) await LoadFolderAsync(path);
    }

    private readonly List<LibraryRow> _folderRows = [];

    private LibraryRow RowFor(Track t)
    {
        bool played;
        lock (_sessionPlayed) played = _sessionPlayed.Contains(t.Path);
        return new LibraryRow(t) { Bpm = _tracks.Get(t.Path)?.DisplayBpm, IsPlayed = played };
    }

    public const int RecentlyPlayedLimit = 100;

    /// <summary>Paths played since the app started, for the library's tick.</summary>
    private readonly HashSet<string> _sessionPlayed = [];

    /// <summary>Records a play in the history and ticks the track in the list.</summary>
    public void MarkPlayed(Track track)
    {
        _tracks.MarkPlayed(track.Path, DateTime.UtcNow);
        lock (_sessionPlayed) _sessionPlayed.Add(track.Path);
        foreach (LibraryRow row in _folderRows.Where(r => r.Track.Path == track.Path))
            row.IsPlayed = true;
    }

    /// <summary>Lists the most recently played tracks that still exist, newest first.</summary>
    public async Task ShowRecentlyPlayedAsync()
    {
        using CancellationTokenSource scan = BeginScan();
        CancellationToken ct = scan.Token;
        try
        {
            var rows = await Task.Run(() => _tracks.RecentlyPlayed(RecentlyPlayedLimit)
                .Where(File.Exists)
                .Select(path => { ct.ThrowIfCancellationRequested(); return RowFor(_readTrack(path)); })
                .ToList(), ct);
            ct.ThrowIfCancellationRequested();
            SetFolderRows(rows);
            Report($"{rows.Count} recently played track(s)");
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        finally { EndScan(scan); }
    }

    /// <summary>
    /// Cancels whatever the browser was reading and makes this the current read.
    /// Only the current read may change the list or the scanning indicator: an
    /// older one can still be finishing a file when it is superseded.
    /// </summary>
    private CancellationTokenSource BeginScan()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        IsScanning = true;
        return _scan;
    }

    private bool IsCurrent(CancellationTokenSource scan) => ReferenceEquals(_scan, scan);

    private void EndScan(CancellationTokenSource scan)
    {
        if (!IsCurrent(scan)) return;
        _scan = null;
        IsScanning = false;
    }

    /// <summary>Replaces what the library lists, and queues it for background analysis.</summary>
    private void SetFolderRows(IEnumerable<LibraryRow> rows)
    {
        _folderRows.Clear();
        _folderRows.AddRange(rows);
        ApplyFilter();
        QueueLibraryAnalysis();
    }

    /// <summary>Lists one folder's audio files, reading tags off the UI thread.</summary>
    private async Task LoadFolderAsync(string path)
    {
        using CancellationTokenSource scan = BeginScan();
        CancellationToken ct = scan.Token;

        CurrentFolder = path;
        Report($"Reading {path}…");
        SetFolderRows([]);

        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<LibraryRow>();
                foreach (string file in Directory.EnumerateFiles(path).Where(SupportedFormats.IsAudio))
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(RowFor(_readTrack(file)));
                }
                return list;
            }, ct);

            ct.ThrowIfCancellationRequested();
            SetFolderRows(found);
            Report($"{found.Count} track(s) in {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        catch (Exception ex) when (IsCurrent(scan)) { Report($"Could not read {path}: {ex.Message}"); }
        catch (Exception) { /* superseded: the folder now shown is not this one */ }
        finally { EndScan(scan); }
    }

    /// <summary>Recursively searches the current folder, replacing the original's blocking scan.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { ApplyFilter(); return; }
        if (!Directory.Exists(CurrentFolder)) return;

        using CancellationTokenSource scan = BeginScan();
        CancellationToken ct = scan.Token;

        Report($"Searching {CurrentFolder}…");
        SetFolderRows([]);
        string needle = SearchText;

        try
        {
            await foreach (string file in LibraryScanner.ScanAsync(CurrentFolder, ct))
            {
                if (!Path.GetFileName(file).Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                LibraryRow row = RowFor(await Task.Run(() => _readTrack(file), ct));
                ct.ThrowIfCancellationRequested();
                _folderRows.Add(row);
                if (InBpmRange(row.Bpm)) BrowserRows.Add(row);
                Report($"{BrowserRows.Count} match(es)…");
            }
            QueueLibraryAnalysis();
            Report($"{BrowserRows.Count} match(es) for \"{needle}\"");
        }
        // Stopped by the user, not superseded by another folder.
        catch (OperationCanceledException) when (IsCurrent(scan)) { Report("Search cancelled"); }
        catch (OperationCanceledException) { }
        finally { EndScan(scan); }
    }

    [RelayCommand]
    private void CancelScan() => _scan?.Cancel();

    private void ApplyFilter()
    {
        BrowserRows.Clear();
        foreach (LibraryRow row in _folderRows.Where(Matches))
            BrowserRows.Add(row);
    }

    private bool Matches(LibraryRow row) =>
        InBpmRange(row.Bpm) &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         row.Track.Display.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         // Deep search matches file names, so a filter re-test must too.
         Path.GetFileName(row.Track.Path).Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ≈ button's window around the audible deck's BPM.</summary>
    public const double MatchBpmRange = 0.06;

    [ObservableProperty] private decimal? _bpmMin;
    [ObservableProperty] private decimal? _bpmMax;

    /// <summary>Analyse every track in an opened folder, lowest priority. Costs battery on phones.</summary>
    [ObservableProperty] private bool _analyseLibraryBpm;

    partial void OnBpmMinChanged(decimal? value) => ApplyFilter();
    partial void OnBpmMaxChanged(decimal? value) => ApplyFilter();
    partial void OnAnalyseLibraryBpmChanged(bool value) => QueueLibraryAnalysis();

    private void QueueLibraryAnalysis() =>
        _analysis.QueueFolder(AnalyseLibraryBpm ? _folderRows.Select(r => r.Track) : []);

    private bool InBpmRange(double? bpm)
    {
        if (BpmMin is null && BpmMax is null) return true;
        if (bpm is null) return false;
        double lo = BpmMin is { } min ? (double)min : 0;
        double hi = BpmMax is { } max ? (double)max : double.MaxValue;
        if (lo > hi) (lo, hi) = (hi, lo);
        return bpm >= lo && bpm <= hi;
    }

    /// <summary>Filters the library to what will mix with the audible deck.</summary>
    [RelayCommand]
    private void MatchBpm()
    {
        if (_audibleBpm() is not { } bpm)
        {
            Report("No BPM to match: the playing deck's BPM isn't known yet");
            return;
        }
        BpmMin = Math.Round((decimal)(bpm * (1 - MatchBpmRange)), 1);
        BpmMax = Math.Round((decimal)(bpm * (1 + MatchBpmRange)), 1);
    }

    /// <summary>Shows a track's new BPM after analysis or a ×½/×2 correction.</summary>
    public void RefreshBpm(string path)
    {
        double? bpm = _tracks.Get(path)?.DisplayBpm;
        foreach (LibraryRow row in _folderRows.Where(r => r.Track.Path == path))
        {
            row.Bpm = bpm;
            // Add a newly matching row without rebuilding the list, so the
            // selection survives a background folder scan.
            if (Matches(row) && !BrowserRows.Contains(row)) BrowserRows.Add(row);
        }
    }

    public void Dispose() => _scan?.Cancel();
}
