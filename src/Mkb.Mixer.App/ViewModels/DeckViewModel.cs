using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>One deck: its transport, its playlist and its waveform.</summary>
public sealed partial class DeckViewModel : ViewModelBase
{
    private readonly IDeck _deck;
    private readonly IAudioEngine _engine;
    private readonly Random _random;
    private CancellationTokenSource? _analysis;
    private Track? _lastDequeued;

    /// <param name="random">Injectable so tests can make shuffle deterministic.</param>
    public DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null)
    {
        _deck = deck;
        _engine = engine;
        _random = random ?? Random.Shared;
        // SoundFlow raises this from its audio callback thread, so it has to be
        // marshalled before it touches anything bound to the UI.
        _deck.TrackEnded += (_, _) => Dispatcher.UIThread.Post(PlayNextFromPlaylist);
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
    }

    /// <summary>Back to normal speed, since dragging the slider to dead centre on touch is fiddly.</summary>
    [RelayCommand]
    private void ResetTempo() => Tempo = 1.0;

    partial void OnIsCuedChanged(bool value) => _deck.IsCued = value;

    /// <summary>Loads a track and kicks off waveform analysis in the background.</summary>
    public async Task LoadAsync(Track track)
    {
        _deck.Load(track);
        NowPlaying = track.Display;
        Waveform = Waveform.Empty;
        Refresh();

        _analysis?.Cancel();
        _analysis = new CancellationTokenSource();
        CancellationToken ct = _analysis.Token;
        try
        {
            Waveform wave = await _engine.AnalyseAsync(track.Path, ct);
            if (!ct.IsCancellationRequested) Waveform = wave;
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

        _deck.Play();
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
        IsPlaying = _deck.State == PlaybackState.Playing;
        TimeSpan pos = _deck.Position, dur = _deck.Duration;
        Elapsed = Format(pos);
        Remaining = "-" + Format(dur - pos);
        Progress = dur > TimeSpan.Zero ? pos.TotalSeconds / dur.TotalSeconds : 0;
        if (_deck.Track is { } t && NowPlaying != t.Display) NowPlaying = t.Display;
    }

    private static string Format(TimeSpan t) =>
        t < TimeSpan.Zero ? "0:00" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
