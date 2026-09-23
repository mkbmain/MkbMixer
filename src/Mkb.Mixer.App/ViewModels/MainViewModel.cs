using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly AutoCue _autoCue;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _scan;
    private bool _suppressFaderFeedback;

    public MainViewModel() : this(new SoundFlowAudioEngine(), SettingsStore.Default()) { }

    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();

        DeckA = new DeckViewModel(engine.DeckA, engine);
        DeckB = new DeckViewModel(engine.DeckB, engine);

        _autoCue = new AutoCue(engine.DeckA, engine.DeckB, DequeueFor)
        {
            Enabled = _settings.AutoCueEnabled,
            CrossfadeDuration = TimeSpan.FromSeconds(_settings.CrossfadeSeconds),
            CrossfaderPosition = _settings.CrossfaderPosition
        };
        _autoCue.CrossfaderMoved += (_, pos) =>
        {
            // The auto-cue owns the fader during a transition; echo it to the slider
            // without treating that as a user gesture.
            _suppressFaderFeedback = true;
            CrossfaderPosition = pos;
            _suppressFaderFeedback = false;
        };

        _crossfaderPosition = _settings.CrossfaderPosition;
        _autoCueEnabled = _settings.AutoCueEnabled;
        _crossfadeSeconds = _settings.CrossfadeSeconds;
        _currentFolder = _settings.LastFolder ?? DefaultFolder();
        engine.ApplyCrossfader(_crossfaderPosition);

        if (!engine.IsOutputAvailable)
            StatusMessage = $"No audio output: {engine.OutputError}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public DeckViewModel DeckA { get; }
    public DeckViewModel DeckB { get; }

    /// <summary>Folders in the left-hand browser tree.</summary>
    public ObservableCollection<FolderNode> Roots { get; } = [];

    /// <summary>Audio files found in the selected folder, filtered by the search box.</summary>
    public ObservableCollection<Track> BrowserTracks { get; } = [];

    [ObservableProperty] private FolderNode? _selectedFolder;
    [ObservableProperty] private Track? _selectedBrowserTrack;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _currentFolder = string.Empty;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private float _crossfaderPosition;
    [ObservableProperty] private bool _autoCueEnabled;
    [ObservableProperty] private int _crossfadeSeconds;

    partial void OnCrossfaderPositionChanged(float value)
    {
        _engine.ApplyCrossfader(value);
        if (!_suppressFaderFeedback)
            _autoCue.CrossfaderPosition = value;
    }

    partial void OnAutoCueEnabledChanged(bool value) => _autoCue.Enabled = value;

    partial void OnCrossfadeSecondsChanged(int value) =>
        _autoCue.CrossfadeDuration = TimeSpan.FromSeconds(Math.Max(1, value));

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        if (value is not null) _ = LoadFolderAsync(value.Path);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private Track? DequeueFor(DeckId id) =>
        (id == DeckId.A ? DeckA : DeckB).DequeueNext();

    private void OnTick(object? sender, EventArgs e)
    {
        DeckA.Refresh();
        DeckB.Refresh();
        ClockText = DateTime.Now.ToLongTimeString();
        _autoCue.Tick(_clock.Elapsed);
    }

    private static string DefaultFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } m && Directory.Exists(m)
            ? m
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Populates the browser roots. The original enumerated Windows drive letters,
    /// which has no meaning on Linux or macOS; this uses the user's real folders.
    /// </summary>
    public void LoadRoots()
    {
        Roots.Clear();
        foreach (string path in CandidateRoots().Where(Directory.Exists).Distinct())
            Roots.Add(new FolderNode(path));
    }

    private static System.Collections.Generic.IEnumerable<string> CandidateRoots()
    {
        yield return DefaultFolder();
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

    private readonly System.Collections.Generic.List<Track> _folderTracks = [];

    /// <summary>Lists one folder's audio files, reading tags off the UI thread.</summary>
    private async Task LoadFolderAsync(string path)
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        CancellationToken ct = _scan.Token;

        CurrentFolder = path;
        IsScanning = true;
        StatusMessage = $"Reading {path}…";
        _folderTracks.Clear();
        BrowserTracks.Clear();

        try
        {
            var found = await Task.Run(() =>
            {
                var list = new System.Collections.Generic.List<Track>();
                foreach (string file in Directory.EnumerateFiles(path).Where(SupportedFormats.IsAudio))
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(TrackMetadataReader.Read(file));
                }
                return list;
            }, ct);

            _folderTracks.AddRange(found);
            ApplyFilter();
            StatusMessage = $"{found.Count} track(s) in {Path.GetFileName(path)}";
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        catch (Exception ex) { StatusMessage = $"Could not read {path}: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    /// <summary>Recursively searches the current folder, replacing the original's blocking scan.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { ApplyFilter(); return; }
        if (!Directory.Exists(CurrentFolder)) return;

        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        CancellationToken ct = _scan.Token;

        IsScanning = true;
        StatusMessage = $"Searching {CurrentFolder}…";
        BrowserTracks.Clear();
        _folderTracks.Clear();
        string needle = SearchText;

        try
        {
            await foreach (string file in LibraryScanner.ScanAsync(CurrentFolder, ct))
            {
                if (!Path.GetFileName(file).Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                Track t = await Task.Run(() => TrackMetadataReader.Read(file), ct);
                _folderTracks.Add(t);
                BrowserTracks.Add(t);
                StatusMessage = $"{BrowserTracks.Count} match(es)…";
            }
            StatusMessage = $"{BrowserTracks.Count} match(es) for \"{needle}\"";
        }
        catch (OperationCanceledException) { StatusMessage = "Search cancelled"; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void CancelScan() => _scan?.Cancel();

    private void ApplyFilter()
    {
        BrowserTracks.Clear();
        foreach (Track t in _folderTracks.Where(Matches))
            BrowserTracks.Add(t);
    }

    private bool Matches(Track t) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        t.Display.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void AddToDeckA() => AddToDeck(DeckA);

    [RelayCommand]
    private void AddToDeckB() => AddToDeck(DeckB);

    private void AddToDeck(DeckViewModel deck)
    {
        if (SelectedBrowserTrack is { } t) deck.Playlist.Add(t);
    }

    [RelayCommand]
    private void SendAToB() => Transfer(DeckA, DeckB);

    [RelayCommand]
    private void SendBToA() => Transfer(DeckB, DeckA);

    private static void Transfer(DeckViewModel from, DeckViewModel to)
    {
        if (from.SelectedPlaylistItem is not { } t) return;
        from.Playlist.Remove(t);
        to.Playlist.Add(t);
    }

    /// <summary>Saves both playlists next to the settings file.</summary>
    [RelayCommand]
    private void SavePlaylists()
    {
        string dir = PlaylistDirectory();
        M3uPlaylist.Save(Path.Combine(dir, "deck-a.m3u"), DeckA.Playlist);
        M3uPlaylist.Save(Path.Combine(dir, "deck-b.m3u"), DeckB.Playlist);
        StatusMessage = $"Playlists saved to {dir}";
    }

    [RelayCommand]
    private void LoadPlaylists()
    {
        string dir = PlaylistDirectory();
        Restore(DeckA, Path.Combine(dir, "deck-a.m3u"));
        Restore(DeckB, Path.Combine(dir, "deck-b.m3u"));
        StatusMessage = "Playlists loaded";

        static void Restore(DeckViewModel deck, string path)
        {
            var loaded = M3uPlaylist.Load(path).ToList();
            if (loaded.Count == 0) return;
            deck.Playlist.Clear();
            foreach (Track t in loaded) deck.Playlist.Add(t);
        }
    }

    private static string PlaylistDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MkbMixer", "playlists");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Persists settings and playlists on the way out.</summary>
    public void SaveState(double windowWidth, double windowHeight)
    {
        _settings.LastFolder = CurrentFolder;
        _settings.CrossfadeSeconds = CrossfadeSeconds;
        _settings.AutoCueEnabled = AutoCueEnabled;
        _settings.CrossfaderPosition = CrossfaderPosition;
        _settings.WindowWidth = windowWidth;
        _settings.WindowHeight = windowHeight;
        _settingsStore.Save(_settings);
        SavePlaylists();
    }

    public double SavedWidth => _settings.WindowWidth;
    public double SavedHeight => _settings.WindowHeight;

    public void Dispose()
    {
        _timer.Stop();
        _scan?.Cancel();
        _engine.Dispose();
    }
}
