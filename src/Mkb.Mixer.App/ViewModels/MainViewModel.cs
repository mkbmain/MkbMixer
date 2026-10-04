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
    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _scan;
    private bool _suppressFaderFeedback;
    private readonly System.Collections.Generic.List<DeckViewModel> _pausedByPauseAll = [];

    public MainViewModel() : this(new SoundFlowAudioEngine(), SettingsStore.Default()) { }

    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();

        DeckA = new DeckViewModel(engine.DeckA, engine);
        DeckB = new DeckViewModel(engine.DeckB, engine);
        DeckA.PlaybackRefused += (_, why) => StatusMessage = why;
        DeckB.PlaybackRefused += (_, why) => StatusMessage = why;

        DeckA.Shuffle = _settings.DeckAShuffle;
        DeckA.Repeat = _settings.DeckARepeat;
        DeckB.Shuffle = _settings.DeckBShuffle;
        DeckB.Repeat = _settings.DeckBRepeat;

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

        // Silent playback is confusing unless the app says why, so the audio route
        // is always visible rather than only on failure.
        if (engine.IsOutputAvailable)
        {
            AudioStatus = $"♪ {engine.OutputDescription}";
        }
        else
        {
            AudioStatus = "♪ no audio output";
            StatusMessage = $"No audio output ({engine.OutputError}). "
                          + "Run with --audio-info to see which backends work.";
        }

        foreach (string line in engine.Diagnostics)
            Console.WriteLine($"[audio] {line}");

        // Restore the headphone cue last, so if it cannot be re-applied (USB
        // headphones unplugged since last time) its message is the one shown.
        _cueMix = _settings.CueMix;
        engine.CueMix = _cueMix;
        _cueDevice = _settings.CueDevice;
        CueMode = _settings.CueMode;
    }

    /// <summary>
    /// Starts the transport clock. Separate from the constructor because a
    /// <see cref="DispatcherTimer"/> binds to the dispatcher of the thread that
    /// creates it, which would make this view model unconstructible off the UI
    /// thread. The window calls this once it is open.
    /// </summary>
    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Drives one transport update. Exposed so tests can step it directly.</summary>
    public void Tick(TimeSpan now)
    {
        DeckA.Refresh();
        DeckB.Refresh();
        ClockText = DateTime.Now.ToLongTimeString();
        _autoCue.Tick(now);
        if (CueMode == CueMode.Device && _engine.CueFault is { } fault)
        {
            // Switching off tears the device down, so its stream cannot be moved to
            // the room speakers by the backend.
            CueMode = CueMode.Off;
            StatusMessage = $"Headphone cue is off: {fault}";
        }
        PlayingSummary = (DeckA.IsPlaying, DeckB.IsPlaying) switch
        {
            (true, true) => $"{DeckA.NowPlaying} → {DeckB.NowPlaying}",
            (true, false) => DeckA.NowPlaying,
            (false, true) => DeckB.NowPlaying,
            _ => string.Empty
        };
        // After the summary, so whoever reacts to this sees the matching text.
        IsAnyDeckPlaying = DeckA.IsPlaying || DeckB.IsPlaying;
    }

    /// <summary>
    /// Pauses every playing deck and remembers which, for the platform's media
    /// controls and audio focus. A call with nothing playing leaves the memory of
    /// the previous pause intact, so a phone call arriving after a lock-screen pause
    /// cannot make the later resume forget what to restart.
    /// </summary>
    /// <returns>True when something was paused.</returns>
    public bool PauseAll()
    {
        DeckViewModel[] playing = new[] { DeckA, DeckB }
            .Where(d => d.State == PlaybackState.Playing)
            .ToArray();
        if (playing.Length == 0) return false;

        _pausedByPauseAll.Clear();
        foreach (DeckViewModel deck in playing)
        {
            deck.PauseCommand.Execute(null);
            _pausedByPauseAll.Add(deck);
        }
        return true;
    }

    /// <summary>
    /// Resumes what <see cref="PauseAll"/> paused. With nothing remembered and
    /// nothing playing, it presses play on the deck in front on the crossfader.
    /// </summary>
    public void ResumePaused()
    {
        DeckViewModel[] toResume = _pausedByPauseAll
            .Where(d => d.State == PlaybackState.Paused)
            .ToArray();
        _pausedByPauseAll.Clear();

        if (toResume.Length > 0)
        {
            foreach (DeckViewModel deck in toResume)
                deck.PlayCommand.Execute(null);
            return;
        }

        if (DeckA.State == PlaybackState.Playing || DeckB.State == PlaybackState.Playing)
            return;

        (CrossfaderPosition <= Crossfader.Centre ? DeckA : DeckB).PlayCommand.Execute(null);
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
    [ObservableProperty] private string _audioStatus = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private float _crossfaderPosition;
    [ObservableProperty] private bool _autoCueEnabled;
    [ObservableProperty] private int _crossfadeSeconds;

    public System.Collections.Generic.IReadOnlyList<CueMode> CueModes { get; } =
        [CueMode.Off, CueMode.Split, CueMode.Device];

    /// <summary>Outputs the headphones can be on, in Device mode.</summary>
    public ObservableCollection<string> CueDevices { get; } = [];

    [ObservableProperty] private CueMode _cueMode;
    [ObservableProperty] private string? _cueDevice;

    /// <summary>What the headphones hear: 0 is cued decks only, 1 is the room mix only.</summary>
    [ObservableProperty] private float _cueMix;

    public bool IsCueDeviceMode => CueMode == CueMode.Device;

    private bool _applyingCue;

    /// <summary>
    /// True while either deck is audible. Android keeps a foreground service running
    /// off this, so the mix carries on with the screen off.
    /// </summary>
    [ObservableProperty] private bool _isAnyDeckPlaying;

    /// <summary>What is audible right now, for the playback notification.</summary>
    [ObservableProperty] private string _playingSummary = string.Empty;

    /// <summary>
    /// Replaces the default browser roots when the platform knows better. Android
    /// supplies its mounted storage volumes here, SD cards and USB drives included.
    /// </summary>
    public Func<System.Collections.Generic.IEnumerable<StorageRoot>>? PlatformRoots { get; set; }

    partial void OnCrossfaderPositionChanged(float value)
    {
        _engine.ApplyCrossfader(value);
        if (!_suppressFaderFeedback)
            _autoCue.CrossfaderPosition = value;
    }

    partial void OnAutoCueEnabledChanged(bool value) => _autoCue.Enabled = value;

    partial void OnCrossfadeSecondsChanged(int value) =>
        _autoCue.CrossfadeDuration = TimeSpan.FromSeconds(Math.Max(1, value));

    partial void OnCueModeChanged(CueMode value)
    {
        OnPropertyChanged(nameof(IsCueDeviceMode));
        if (value == CueMode.Device) RefreshCueDevices();
        ApplyCue();
    }

    partial void OnCueDeviceChanged(string? value)
    {
        if (CueMode == CueMode.Device) ApplyCue();
    }

    partial void OnCueMixChanged(float value) => _engine.CueMix = value;

    private void RefreshCueDevices()
    {
        _applyingCue = true;   // filling the list must not reroute once per item
        try
        {
            string? previous = CueDevice;   // clearing a bound ComboBox pushes null back
            CueDevices.Clear();
            foreach (string name in _engine.CueDeviceNames())
                CueDevices.Add(name);
            // Put the choice back and never fill a blank one: the first listed
            // output could be anything, and a wrong guess puts the cue on the room
            // speakers. A remembered device that has gone missing is kept so
            // applying it fails loudly instead.
            CueDevice = previous;
        }
        finally { _applyingCue = false; }
    }

    private void ApplyCue()
    {
        if (_applyingCue) return;
        _applyingCue = true;
        try
        {
            if (CueMode == CueMode.Device && CueDevice is null)
            {
                // Device is selected so the list shows, but nothing routes until the
                // DJ picks an output.
                _engine.TrySetCue(CueMode.Off, null, out _);
                StatusMessage = "Headphones: choose the output your headphones are plugged into";
            }
            else if (!_engine.TrySetCue(CueMode, CueDevice, out string? error))
            {
                StatusMessage = $"Headphone cue is off: {error}";
                CueMode = CueMode.Off;
                _engine.TrySetCue(CueMode.Off, null, out _);
            }
            DeckA.IsCueAvailable = DeckB.IsCueAvailable = _engine.CueMode != CueMode.Off;
        }
        finally { _applyingCue = false; }
    }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        // The "…" placeholder stands in for unexpanded children and is not a real
        // path; browsing it would fail with "could not find a part of the path".
        if (value is { IsPlaceholder: false })
            _ = LoadFolderAsync(value.Path);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private Track? DequeueFor(DeckId id) =>
        (id == DeckId.A ? DeckA : DeckB).DequeueNext();

    private void OnTick(object? sender, EventArgs e) => Tick(_clock.Elapsed);

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
        if (PlatformRoots is not null)
        {
            foreach (StorageRoot root in PlatformRoots().Where(r => Directory.Exists(r.Path)).DistinctBy(r => r.Path))
                Roots.Add(new FolderNode(root.Path, root.Name));
            return;
        }
        foreach (string path in CandidateRoots().Where(Directory.Exists).Distinct())
            Roots.Add(new FolderNode(path));
    }

    private static System.Collections.Generic.IEnumerable<string> CandidateRoots()
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
        _settings.WindowWidth = windowWidth;
        _settings.WindowHeight = windowHeight;
        SaveState();
    }

    /// <summary>
    /// Persists settings and playlists without touching the window size. Android has
    /// no window and no reliable "closing" moment, so it calls this when paused.
    /// </summary>
    public void SaveState()
    {
        _settings.LastFolder = CurrentFolder;
        _settings.CrossfadeSeconds = CrossfadeSeconds;
        _settings.AutoCueEnabled = AutoCueEnabled;
        _settings.CrossfaderPosition = CrossfaderPosition;
        _settings.DeckAShuffle = DeckA.Shuffle;
        _settings.DeckARepeat = DeckA.Repeat;
        _settings.DeckBShuffle = DeckB.Shuffle;
        _settings.DeckBRepeat = DeckB.Repeat;
        _settings.CueMode = CueMode;
        _settings.CueDevice = CueDevice;
        _settings.CueMix = CueMix;
        _settingsStore.Save(_settings);
        SavePlaylists();
    }

    public double SavedWidth => _settings.WindowWidth;
    public double SavedHeight => _settings.WindowHeight;

    public void Dispose()
    {
        _timer?.Stop();
        _scan?.Cancel();
        _engine.Dispose();
    }
}
