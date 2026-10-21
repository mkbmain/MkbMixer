using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.App.Services;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly TrackStore _tracks;
    private readonly AutoCue _autoCue;
    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _suppressFaderFeedback;
    private readonly List<DeckViewModel> _pausedByPauseAll = [];

    public AnalysisQueue Analysis { get; }

    public LibraryViewModel Library { get; }

    public MainViewModel() : this(new SoundFlowAudioEngine(), SettingsStore.Default(), TrackStore.Default(),
        post: a => Dispatcher.UIThread.Post(a)) { }

    /// <summary>Remembers nothing per track between runs. For tests.</summary>
    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore)
        : this(engine, settingsStore, TrackStore.InMemory()) { }

    /// <param name="post">How analysis results reach the UI thread; see <see cref="AnalysisQueue"/>.</param>
    /// <param name="manualAnalysis">No background worker; tests drive <see cref="Analysis"/> themselves.</param>
    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore, TrackStore tracks,
                         Action<Action>? post = null, bool manualAnalysis = false)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        _tracks = tracks;
        Analysis = new AnalysisQueue(engine, tracks, post, manualAnalysis);
        Library = new LibraryViewModel(tracks, Analysis, () => AudibleDeck().HeardBpm, _settings.LastFolder,
            analyseBpm: _settings.AnalyseLibraryBpm ?? !App.UsePhoneLayout);
        Library.Status += (_, message) => StatusMessage = message;

        DeckA = new DeckViewModel(engine.DeckA, engine, analysis: Analysis);
        DeckB = new DeckViewModel(engine.DeckB, engine, analysis: Analysis);
        DeckA.Other = DeckB;
        DeckB.Other = DeckA;
        DeckA.TrackPlayed += (_, t) => Library.MarkPlayed(t);
        DeckB.TrackPlayed += (_, t) => Library.MarkPlayed(t);
        DeckA.PlaybackRefused += (_, why) => StatusMessage = why;
        DeckB.PlaybackRefused += (_, why) => StatusMessage = why;

        DeckA.Shuffle = _settings.DeckAShuffle;
        DeckA.Repeat = _settings.DeckARepeat;
        DeckB.Shuffle = _settings.DeckBShuffle;
        DeckB.Repeat = _settings.DeckBRepeat;

        _autoCue = new AutoCue(engine.DeckA, engine.DeckB, DequeueFor, TimingFor)
        {
            Enabled = _settings.AutoCueEnabled,
            CrossfadeDuration = TimeSpan.FromSeconds(_settings.CrossfadeSeconds),
            CrossfaderPosition = _settings.CrossfaderPosition
        };
        TempoMatch = _settings.AutoCueTempoMatch;
        DeckA.UserChangedTempo += (_, _) => _autoCue.CancelGlide(DeckId.A);
        DeckB.UserChangedTempo += (_, _) => _autoCue.CancelGlide(DeckId.B);
        _autoCue.LoadFailed += (_, why) => StatusMessage = why;
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
        _crossfadeSeconds = _settings.CrossfadeSeconds;   // at least 1: SettingsStore.Load sees to that
        Analysis.Analysed += (_, path) => _timings.Remove(path);
        DeckA.BpmCorrected += (_, path) => { _timings.Remove(path); Library.RefreshBpm(path); };
        DeckB.BpmCorrected += (_, path) => { _timings.Remove(path); Library.RefreshBpm(path); };
        DeckA.CuesChanged += (_, path) => _timings.Remove(path);
        DeckB.CuesChanged += (_, path) => _timings.Remove(path);
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

    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _audioStatus = string.Empty;
    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private float _crossfaderPosition;
    [ObservableProperty] private bool _autoCueEnabled;

    [ObservableProperty] private TempoMatchMode _tempoMatch;

    /// <summary>For the combo box, whose items are in enum order.</summary>
    public int TempoMatchIndex
    {
        get => (int)TempoMatch;
        set => TempoMatch = (TempoMatchMode)Math.Clamp(value, 0, 2);
    }

    partial void OnTempoMatchChanged(TempoMatchMode value)
    {
        _autoCue.TempoMatch = value;
        OnPropertyChanged(nameof(TempoMatchIndex));
    }
    [ObservableProperty] private int _crossfadeSeconds;

    public IReadOnlyList<CueMode> CueModes { get; } =
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

    /// <summary>Hot cue 1 beats the detected first sound as the place to bring a track in.</summary>
    private TrackTiming? TimingFor(Track track)
    {
        // The auto-cue asks every tick; keep it off the disk (Get stats the file).
        // Dropped whenever the analysis, a hot cue or the BPM correction changes.
        if (_timings.TryGetValue(track.Path, out TrackTiming? cached)) return cached;
        return _timings[track.Path] = ComputeTiming(track);
    }

    private readonly Dictionary<string, TrackTiming?> _timings = new();

    private TrackTiming? ComputeTiming(Track track)
    {
        if (_tracks.Get(track.Path) is not { } info) return null;
        return new TrackTiming(
            Seconds(info.HotCueSeconds[0] ?? info.FirstSoundSeconds),
            Seconds(info.LastSoundSeconds),
            info.DisplayBpm);

        static TimeSpan? Seconds(double? s) => s is { } v ? TimeSpan.FromSeconds(v) : null;
    }

    private Track? DequeueFor(DeckId id) =>
        (id == DeckId.A ? DeckA : DeckB).DequeueNext();

    private void OnTick(object? sender, EventArgs e) => Tick(_clock.Elapsed);

    /// <summary>The deck the room is hearing: the playing one in front on the crossfader.</summary>
    private DeckViewModel AudibleDeck()
    {
        DeckViewModel front = CrossfaderPosition <= Crossfader.Centre ? DeckA : DeckB;
        DeckViewModel back = ReferenceEquals(front, DeckA) ? DeckB : DeckA;
        return !front.IsPlaying && back.IsPlaying ? back : front;
    }

    [RelayCommand]
    private void AddToDeckA() => AddToDeck(DeckA);

    [RelayCommand]
    private void AddToDeckB() => AddToDeck(DeckB);

    private void AddToDeck(DeckViewModel deck)
    {
        if (Library.SelectedBrowserRow is { } row) deck.Playlist.Add(row.Track);
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
        // Also runs on every Android pause: a full or read-only disk must not crash.
        try
        {
            string dir = PlaylistDirectory();
            M3uPlaylist.Save(Path.Combine(dir, "deck-a.m3u"), DeckA.Playlist);
            M3uPlaylist.Save(Path.Combine(dir, "deck-b.m3u"), DeckB.Playlist);
            StatusMessage = $"Playlists saved to {dir}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not save playlists: {e.Message}";
        }
    }

    [RelayCommand]
    private void LoadPlaylists()
    {
        try
        {
            string dir = PlaylistDirectory();
            Restore(DeckA, Path.Combine(dir, "deck-a.m3u"));
            Restore(DeckB, Path.Combine(dir, "deck-b.m3u"));
            StatusMessage = "Playlists loaded";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not load playlists: {e.Message}";
        }

        static void Restore(DeckViewModel deck, string path)
        {
            var loaded = M3uPlaylist.Load(path).ToList();
            if (loaded.Count == 0) return;
            deck.Playlist.Clear();
            foreach (Track t in loaded) deck.Playlist.Add(t);
        }
    }

    /// <summary>Where playlists are kept. Settable so tests stay out of the real one.</summary>
    public string PlaylistFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MkbMixer", "playlists");

    private string PlaylistDirectory()
    {
        Directory.CreateDirectory(PlaylistFolder);
        return PlaylistFolder;
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
        _settings.LastFolder = Library.CurrentFolder;
        _settings.CrossfadeSeconds = CrossfadeSeconds;
        _settings.AutoCueEnabled = AutoCueEnabled;
        _settings.AutoCueTempoMatch = TempoMatch;
        _settings.CrossfaderPosition = CrossfaderPosition;
        _settings.DeckAShuffle = DeckA.Shuffle;
        _settings.DeckARepeat = DeckA.Repeat;
        _settings.DeckBShuffle = DeckB.Shuffle;
        _settings.DeckBRepeat = DeckB.Repeat;
        _settings.CueMode = CueMode;
        _settings.CueDevice = CueDevice;
        _settings.CueMix = CueMix;
        _settings.AnalyseLibraryBpm = Library.AnalyseLibraryBpm;
        _settingsStore.Save(_settings);
        SavePlaylists();
        _tracks.Flush();
    }

    public double SavedWidth => _settings.WindowWidth;
    public double SavedHeight => _settings.WindowHeight;

    public void Dispose()
    {
        _timer?.Stop();
        Library.Dispose();
        Analysis.Dispose();
        _tracks.Dispose();
        _engine.Dispose();
    }
}
