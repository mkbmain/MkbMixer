using SoundFlow.Structs;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Providers;

namespace Mkb.Mixer.Audio;

/// <summary>A single deck backed by a SoundFlow <see cref="SoundPlayer"/>.</summary>
internal sealed class SoundFlowDeck : IDeck
{
    private readonly MiniAudioEngine? _engine;
    private readonly SoundFlow.Components.Mixer? _output;
    private readonly Lock _gate = new();
    private readonly AudioFormat _format;

    private SoundPlayer? _player;
    private TimeStretchProvider? _provider;
    private Stream? _stream;
    private float _volume = 1f;
    private float _tempo = 1f;
    private bool _muted;
    private readonly CueTap _tap;

    /// <param name="format">What to decode to: the rate the output device actually opened at.</param>
    public SoundFlowDeck(DeckId id, MiniAudioEngine? engine, SoundFlow.Components.Mixer? output, CueBus cueBus,
                         AudioFormat format)
    {
        Id = id;
        _format = format;
        _engine = engine;
        _output = output;
        // One tap for the deck's lifetime, moved onto each new player in Load, so
        // the cue stays on across track changes.
        _tap = new CueTap(cueBus);
    }

    public DeckId Id { get; }
    public Track? Track { get; private set; }

    public PlaybackState State => _player?.State switch
    {
        SoundFlow.Enums.PlaybackState.Playing => PlaybackState.Playing,
        SoundFlow.Enums.PlaybackState.Paused => PlaybackState.Paused,
        _ => PlaybackState.Stopped
    };

    /// <summary>Read from the stretcher: the player's own clock counts output, not track time.</summary>
    public TimeSpan Position =>
        _provider is null ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)_provider.Position
                / _format.Channels / _format.SampleRate);

    public TimeSpan Duration =>
        _player is null ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, _player.Duration));

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyGain();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set { _muted = value; ApplyGain(); }
    }

    public bool IsCued
    {
        get => _tap.IsCued;
        set => _tap.IsCued = value;
    }

    public float Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, 0.5f, 1.5f);
            if (_provider is not null) _provider.Speed = _tempo;
        }
    }

    public event EventHandler? TrackEnded;

    /// <summary>Mute is applied as a gain of zero so it composes with the crossfader.</summary>
    private void ApplyGain()
    {
        if (_player is not null)
            _player.Volume = _muted ? 0f : _volume;
    }

    public void Load(Track track)
    {
        lock (_gate)
        {
            TearDownPlayer();
            Track = track;

            // With no audio context there is nothing to decode into; the track is
            // still recorded so the UI shows what is cued up.
            if (_engine is null) return;

            try
            {
                _stream = File.OpenRead(track.Path);
                _provider = new TimeStretchProvider(
                    new StreamDataProvider(_engine, _format, _stream),
                    _format.Channels) { Speed = _tempo };
                // The player stays at 1.0; the provider does the stretching, far cheaper.
                _player = new SoundPlayer(_engine, _format, _provider)
                {
                    Name = $"Deck{Id}",
                    Volume = _muted ? 0f : _volume
                };
                _player.PlaybackEnded += OnPlaybackEnded;
                _player.AddModifier(_tap);
                _output?.AddComponent(_player);
            }
            catch
            {
                // Release the half-opened file and leave the deck empty, so it never
                // shows a track it cannot play.
                TearDownPlayer();
                Track = null;
                throw;
            }
        }
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) =>
        TrackEnded?.Invoke(this, EventArgs.Empty);

    public void Play() => _player?.Play();
    public void Pause() => _player?.Pause();

    public void Stop()
    {
        _player?.Stop();
        _player?.Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (_player is null) return;
        TimeSpan clamped = position < TimeSpan.Zero ? TimeSpan.Zero
            : position > Duration ? Duration
            : position;
        _player.Seek(clamped);
    }

    private void TearDownPlayer()
    {
        if (_player is not null)
        {
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.Stop();
            _output?.RemoveComponent(_player);
            _player.RemoveModifier(_tap);
            _player.Dispose();
            _player = null;
        }
        _provider?.Dispose();
        _provider = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        lock (_gate) TearDownPlayer();
    }
}
