using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.App.Services;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>One deck: its transport, its playlist and its waveform.</summary>
public sealed partial class DeckViewModel : ViewModelBase
{
    private readonly IDeck _deck;
    private readonly IAudioEngine _engine;
    private readonly Random _random;
    private readonly AnalysisQueue _analysis;
    private CancellationTokenSource? _analysisCts;
    private Track? _currentTrack;
    private Track? _lastDequeued;

    /// <param name="random">Injectable so tests can make shuffle deterministic.</param>
    /// <param name="analysis">Shared with the other deck and the library; a private in-memory one when omitted.</param>
    public DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null, AnalysisQueue? analysis = null)
    {
        _deck = deck;
        _engine = engine;
        _random = random ?? Random.Shared;
        _analysis = analysis ?? new AnalysisQueue(engine, TrackStore.InMemory(), manual: true);
        // SoundFlow raises this from its audio callback thread, so it has to be
        // marshalled before it touches anything bound to the UI.
        _analysis.Analysed += (_, path) =>
        {
            if (_deck.Track?.Path == path) ApplyStoredInfo();
        };
        _deck.TrackEnded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ReportPlayed();
            PlayNextFromPlaylist();
        });
    }

    public const double NudgeAmount = 0.04;
    private double? _nudgeFrom;
    private string _syncHint = string.Empty;

    /// <summary>The track's BPM at normal speed, after any x1/2 or x2 correction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeardBpm), nameof(BpmText))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private double? _trackBpm;

    /// <summary>The BPM coming out of the speakers: the track's BPM times the tempo.</summary>
    public double? HeardBpm => TrackBpm * Tempo;

    public string BpmText => HeardBpm is { } b ? $"{b.ToString("0.0", CultureInfo.InvariantCulture)} BPM" : "— BPM";

    /// <summary>The other deck, for SYNC. Set once by <see cref="MainViewModel"/>.</summary>
    public DeckViewModel? Other { get; set; }

    public bool HasTrack => _deck.Track is not null;

    /// <summary>Why SYNC is unavailable, or what it does when it is.</summary>
    public string SyncHint => SyncBlocker ?? "Match this deck's tempo to the other deck";

    private string? SyncBlocker =>
        TrackBpm is null ? "This deck's BPM isn't known yet"
        : Other is not { HasTrack: true } ? "Nothing is loaded on the other deck"
        : Other.HeardBpm is null ? "The other deck's BPM isn't known yet"
        : null;

    private bool CanSync() => SyncBlocker is null;

    /// <summary>One-shot: sets this deck's tempo so it plays at the other deck's BPM.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private void Sync()
    {
        if (TrackBpm is not { } mine || Other?.HeardBpm is not { } theirs) return;
        Tempo = Math.Clamp(theirs / mine, 0.5, 1.5);
    }

    [RelayCommand]
    private void HalveBpm() => ScaleBpm(0.5);

    [RelayCommand]
    private void DoubleBpm() => ScaleBpm(2);

    private void ScaleBpm(double factor)
    {
        if (_deck.Track is not { } t || TrackBpm is null) return;
        _analysis.Store.Update(t.Path, i => i with { BpmMultiplier = Math.Clamp(i.BpmMultiplier * factor, 0.25, 4) });
        ApplyStoredInfo();
    }

    /// <summary>Holding a nudge button briefly speeds up or slows down, to line beats up by ear.</summary>
    public void BeginNudge(int direction)
    {
        if (_nudgeFrom is not null) return;
        _nudgeFrom = Tempo;
        Tempo = Math.Clamp(Tempo * (1 + NudgeAmount * Math.Sign(direction)), 0.5, 1.5);
    }

    public void EndNudge()
    {
        if (_nudgeFrom is not { } from) return;
        _nudgeFrom = null;
        Tempo = from;
    }

    /// <summary>Pulls what the store knows about the loaded track onto the deck.</summary>
    private void ApplyStoredInfo()
    {
        TrackInfo? info = _deck.Track is { } t ? _analysis.Store.Get(t.Path) : null;
        TrackBpm = info?.DisplayBpm;
        foreach (HotCueSlot slot in HotCues)
            slot.Seconds = info?.HotCueSeconds[slot.Index];
        UpdateCueFractions();
    }

    public IReadOnlyList<HotCueSlot> HotCues { get; } =
        Enumerable.Range(0, TrackInfo.HotCueCount).Select(i => new HotCueSlot(i)).ToArray();

    /// <summary>Each slot's position as 0..1 of the track, for the waveform markers.</summary>
    public double?[] CueFractions { get; private set; } = new double?[TrackInfo.HotCueCount];

    private TimeSpan _cueFractionsDuration;

    /// <summary>Sets an empty slot at the playhead, or jumps to a set one keeping play/stop as it is.</summary>
    [RelayCommand]
    private void HotCue(HotCueSlot slot)
    {
        if (_deck.Track is not { } t) return;
        if (slot.Seconds is { } seconds)
        {
            _deck.Seek(TimeSpan.FromSeconds(seconds));
            Refresh();
            return;
        }
        slot.Seconds = _deck.Position.TotalSeconds;
        SaveHotCue(t, slot);
    }

    [RelayCommand]
    private void ClearHotCue(HotCueSlot slot)
    {
        if (_deck.Track is not { } t || !slot.IsSet) return;
        slot.Seconds = null;
        SaveHotCue(t, slot);
    }

    private void SaveHotCue(Track track, HotCueSlot slot)
    {
        _analysis.Store.Update(track.Path, i => i.WithHotCue(slot.Index, slot.Seconds));
        UpdateCueFractions();
    }

    private void UpdateCueFractions()
    {
        TimeSpan duration = _deck.Duration;
        _cueFractionsDuration = duration;
        CueFractions = HotCues
            .Select(c => c.Seconds is { } s && duration > TimeSpan.Zero
                ? Math.Clamp(s / duration.TotalSeconds, 0, 1)
                : (double?)null)
            .ToArray();
        OnPropertyChanged(nameof(CueFractions));
    }

    public DeckId Id => _deck.Id;
    /// <summary>The deck's live transport state, not the 100 ms-stale <see cref="IsPlaying"/>.</summary>
    public PlaybackState State => _deck.State;
    public string Label => _deck.Id == DeckId.A ? "DECK A" : "DECK B";

    /// <summary>The queue this deck plays through, and that the auto-cue pulls from.</summary>
    public ObservableCollection<Track> Playlist { get; } = [];

    [ObservableProperty] private Track? _selectedPlaylistItem;
    [ObservableProperty] private string _nowPlaying = "No track loaded";
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private string _remaining = "-0:00";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private Waveform _waveform = Waveform.Empty;
    [ObservableProperty] private double _tempo = 1.0;
    /// <summary>Take a random queued track rather than the top one.</summary>
    [ObservableProperty] private bool _shuffle;
    /// <summary>Put each played track back on the end of the queue, so it never runs dry.</summary>
    [ObservableProperty] private bool _repeat;

    /// <summary>Whether this deck is sent to the headphones.</summary>
    [ObservableProperty] private bool _isCued;

    /// <summary>False while the headphone mode is Off, which greys the deck's CUE button.</summary>
    [ObservableProperty] private bool _isCueAvailable;

    public string MuteLabel => IsMuted ? "■ MUTE" : "■ LIVE";

    partial void OnIsMutedChanged(bool value)
    {
        _deck.IsMuted = value;
        OnPropertyChanged(nameof(MuteLabel));
    }

    /// <summary>The tempo as a multiplier, which doubles as the button that resets it.</summary>
    public string TempoLabel => $"{Tempo:0.00}×";

    partial void OnTempoChanged(double value)
    {
        _deck.Tempo = (float)value;
        OnPropertyChanged(nameof(TempoLabel));
        OnPropertyChanged(nameof(HeardBpm));
        OnPropertyChanged(nameof(BpmText));
    }

    /// <summary>Back to normal speed, since dragging the slider to dead centre on touch is fiddly.</summary>
    [RelayCommand]
    private void ResetTempo() => Tempo = 1.0;

    partial void OnIsCuedChanged(bool value) => _deck.IsCued = value;

    /// <summary>Loads a track and kicks off its analysis in the background.</summary>
    public async Task LoadAsync(Track track)
    {
        _deck.Load(track);
        await OnTrackChangedAsync();
    }

    /// <summary>
    /// Catches up with whatever the deck now holds, whether this view model loaded
    /// it or the auto-cue did on the <see cref="IDeck"/> directly. Before this, a
    /// track brought in by the auto-cue kept the previous track's waveform.
    /// </summary>
    private async Task OnTrackChangedAsync()
    {
        Track? track = _deck.Track;
        _currentTrack = track;
        _playedReported = false;
        Waveform = Waveform.Empty;
        ApplyStoredInfo();
        Refresh();
        if (track is null) return;

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        CancellationToken ct = _analysisCts.Token;
        try
        {
            TrackAnalysis analysis = await _analysis.AnalyseForDeckAsync(track, ct);
            if (!ct.IsCancellationRequested)
            {
                Waveform = analysis.Waveform;
                ApplyStoredInfo();
            }
        }
        catch (OperationCanceledException) { /* a newer track superseded this one */ }
    }

    /// <summary>Raised when the user asks for playback that cannot happen, so the UI can say why.</summary>
    public event EventHandler<string>? PlaybackRefused;

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (!_engine.IsOutputAvailable)
        {
            PlaybackRefused?.Invoke(this, $"{Label}: no audio output. "
                + "Run with --audio-info to see which backends work.");
            return;
        }

        // Pressing play with nothing loaded pulls the top of the playlist, which is
        // what the original did implicitly via its selected-item lookup.
        if (_deck.Track is null && Playlist.Count > 0)
            await LoadAsync(Playlist[0]);

        if (_deck.Track is null)
        {
            PlaybackRefused?.Invoke(this, $"{Label}: nothing loaded. "
                + "Add a track to the playlist first.");
            return;
        }

        bool fromStop = _deck.State == PlaybackState.Stopped;
        TimeSpan at = _deck.Position;
        _deck.Play();
        // Some backends restart a stopped player from the top; keep a hot cue
        // jump made while stopped.
        if (fromStop && at > TimeSpan.Zero) _deck.Seek(at);
        Refresh();
    }

    [RelayCommand]
    private void Pause() { _deck.Pause(); Refresh(); }

    [RelayCommand]
    private void Stop() { _deck.Stop(); Refresh(); }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private async Task LoadSelectedAsync()
    {
        if (SelectedPlaylistItem is { } t) await LoadAsync(t);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedPlaylistItem is { } t) Playlist.Remove(t);
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        if (SelectedPlaylistItem is not { } t) return;
        int i = Playlist.IndexOf(t);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Playlist.Count) return;
        Playlist.Move(i, j);
        SelectedPlaylistItem = t;
    }

    /// <summary>
    /// Pops the next queued track, honouring shuffle and repeat. The auto-cue and
    /// "play the next track when this one ends" both come through here.
    /// </summary>
    public Track? DequeueNext()
    {
        if (Playlist.Count == 0) return null;

        int index = 0;
        if (Shuffle && Playlist.Count > 1)
        {
            // Never the track just played, which repeat has put back on the end.
            // Compared by reference: the same library track can be queued twice.
            int[] candidates = Enumerable.Range(0, Playlist.Count)
                .Where(i => !ReferenceEquals(Playlist[i], _lastDequeued))
                .ToArray();
            if (candidates.Length == 0)
                candidates = Enumerable.Range(0, Playlist.Count).ToArray();
            index = candidates[_random.Next(candidates.Length)];
        }

        Track next = Playlist[index];
        Playlist.RemoveAt(index);
        if (Repeat) Playlist.Add(next);
        _lastDequeued = next;
        return next;
    }

    private async void PlayNextFromPlaylist()
    {
        if (DequeueNext() is not { } next) return;
        await LoadAsync(next);
        _deck.Play();
    }

    /// <summary>Seeks to a fraction of the track, used by clicking the waveform.</summary>
    public void SeekToFraction(double fraction)
    {
        if (_deck.Duration <= TimeSpan.Zero) return;
        _deck.Seek(_deck.Duration * Math.Clamp(fraction, 0, 1));
        Refresh();
    }

    /// <summary>Pulls the current transport state out of the deck for the UI.</summary>
    public void Refresh()
    {
        if (!ReferenceEquals(_deck.Track, _currentTrack))
        {
            _ = OnTrackChangedAsync();
            return;   // it calls Refresh again once caught up
        }
        IsPlaying = _deck.State == PlaybackState.Playing;
        TimeSpan pos = _deck.Position, dur = _deck.Duration;
        if (dur != _cueFractionsDuration) UpdateCueFractions();
        Elapsed = Format(pos);
        Remaining = "-" + Format(dur - pos);
        Progress = dur > TimeSpan.Zero ? pos.TotalSeconds / dur.TotalSeconds : 0;
        if (_deck.Track is { } t && NowPlaying != t.Display) NowPlaying = t.Display;
        if (IsPlaying && pos >= PlayedThreshold(dur)) ReportPlayed();
        SyncCommand.NotifyCanExecuteChanged();
        string hint = SyncHint;
        if (hint != _syncHint) { _syncHint = hint; OnPropertyChanged(nameof(SyncHint)); }
    }

    /// <summary>Raised once per loaded track, when it has played long enough to count, or ended.</summary>
    public event EventHandler<Track>? TrackPlayed;

    private bool _playedReported;

    /// <summary>30 s, or 90% of a track shorter than that.</summary>
    public static TimeSpan PlayedThreshold(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration * 0.9 < TimeSpan.FromSeconds(30)
            ? duration * 0.9
            : TimeSpan.FromSeconds(30);

    private void ReportPlayed()
    {
        if (_playedReported || _deck.Track is not { } t) return;
        _playedReported = true;
        TrackPlayed?.Invoke(this, t);
    }

    private static string Format(TimeSpan t) =>
        t < TimeSpan.Zero ? "0:00" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
