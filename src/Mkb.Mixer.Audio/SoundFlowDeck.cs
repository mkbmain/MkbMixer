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

    // Two, not one: DequeueNext prefetches the following track before its caller
    // Loads the dequeued one, so a single slot would always evict the track about to load.
    private const int MaxPrepared = 2;
    private static readonly TimeSpan OpenDisposeTimeout = TimeSpan.FromSeconds(2);
    private readonly List<PreparedChain> _prepared = [];   // oldest first
    // Every open running, including evicted ones no longer in _prepared: Dispose must outlast them all.
    private readonly List<PreparedChain> _opening = [];
    private bool _disposed;

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

    /// <summary>Called as each file is opened, so tests can count opens and hold one running.</summary>
    internal Action<string>? OpeningHook { get; set; }

    /// <summary>What is prepared or being prepared, oldest first.</summary>
    internal IReadOnlyList<string> PreparedPaths
    {
        get { lock (_gate) return _prepared.ConvertAll(p => p.Path); }
    }

    internal TimeStretchProvider? LoadedProvider => _provider;

    public Task PrepareAsync(Track track, CancellationToken ct = default)
    {
        if (_engine is null) return Task.CompletedTask;
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        Task opening;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            PreparedChain? prepared = _prepared.Find(p => p.Path == track.Path);
            if (prepared is null)
            {
                if (_prepared.Count == MaxPrepared) Discard(_prepared[0]);
                PreparedChain added = prepared = new PreparedChain(track.Path);
                _prepared.Add(added);
                _opening.Add(added);
                added.Opening = Task.Run(() => FinishOpening(added));
            }
            opening = prepared.Opening;
        }
        return opening.WaitAsync(ct);
    }

    /// <summary>
    /// Runs on the pool and never faults, so nothing has to observe it. It has no
    /// continuations either: <see cref="Dispose"/> can wait for it on the UI thread.
    /// </summary>
    private void FinishOpening(PreparedChain prepared)
    {
        lock (_gate)
        {
            if (prepared.Discarded)
            {
                _opening.Remove(prepared);
                return;
            }
        }
        Chain? chain = null;
        try { chain = OpenChain(prepared.Path); }
        catch { /* Load opens the file itself and reports what is wrong with it */ }

        lock (_gate)
        {
            _opening.Remove(prepared);
            if (!prepared.Discarded)
            {
                if (chain is null) _prepared.Remove(prepared);
                prepared.Chain = chain;
                return;
            }
        }
        // Lost while it was opening: nobody else knows about this chain.
        chain?.TryDispose();
    }

    /// <summary>Caller holds <see cref="_gate"/>. An open still running sees the flag and disposes its own chain.</summary>
    private void Discard(PreparedChain prepared)
    {
        prepared.Discarded = true;
        _prepared.Remove(prepared);
        prepared.Chain?.TryDispose();
        prepared.Chain = null;
    }

    public void ReleasePrepared()
    {
        lock (_gate)
            foreach (PreparedChain prepared in _prepared.ToArray()) Discard(prepared);
    }

    private Chain OpenChain(string path)
    {
        OpeningHook?.Invoke(path);
        // Before the open, so a file changed in between mismatches rather than passes.
        (long Size, DateTime WriteUtc)? stamp = Stat(path);
        Stream stream = File.OpenRead(path);
        try
        {
            return new Chain(stream, new TimeStretchProvider(
                new StreamDataProvider(_engine!, _format, stream),
                _format.Channels), stamp);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Load(Track track)
    {
        // An open of this track still running finishes sooner than a fresh one would.
        // Waited for outside the gate, which the open needs in order to finish.
        Task? opening;
        lock (_gate) opening = _prepared.Find(p => p.Path == track.Path)?.Opening;
        opening?.Wait();

        lock (_gate)
        {
            TearDownPlayer();
            Track = track;

            // With no audio context there is nothing to decode into; the track is
            // still recorded so the UI shows what is cued up. A disposed deck is the
            // same, and its engine is about to be freed.
            if (_engine is null || _disposed) return;

            try
            {
                Chain chain = TakePrepared(track.Path) ?? OpenChain(track.Path);
                _stream = chain.Stream;
                _provider = chain.Provider;
                // Set here, not when opened: the tempo may have moved since.
                _provider.Speed = _tempo;
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

    /// <summary>Caller holds <see cref="_gate"/>. Taken out of the list, so no second Load can use it.</summary>
    private Chain? TakePrepared(string path)
    {
        if (_prepared.Find(p => p.Path == path && p.Chain is not null) is not { } prepared) return null;
        // On pulled media the descriptor is dead: reads fail, which the decoder reports as
        // the end of the track, so a stale chain would "load" and then play nothing. The
        // open that replaces it throws the real error, or finds the new content.
        if (Stat(path) is not { } now || now != prepared.Chain!.Stamp)
        {
            Discard(prepared);
            return null;
        }
        _prepared.Remove(prepared);
        return prepared.Chain;
    }

    /// <summary>Null when the file is gone or cannot be examined.</summary>
    private static (long Size, DateTime WriteUtc)? Stat(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
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
        Task[] running;
        lock (_gate)
        {
            _disposed = true;
            TearDownPlayer();
            foreach (PreparedChain prepared in _prepared.ToArray()) Discard(prepared);
            running = _opening.ConvertAll(p => p.Opening).ToArray();
        }
        // The engine is freed right after this, and a decoder still opening against it
        // would touch freed native memory. Outside the gate, which an open needs in order
        // to finish; bounded, because a stuck disk must not hold the app open on exit.
        Task.WaitAll(running, OpenDisposeTimeout);
    }

    /// <summary>A stream and the provider chain reading it, not yet given to a player.</summary>
    /// <param name="Stamp">The file's size and write time when it was opened.</param>
    private sealed record Chain(Stream Stream, TimeStretchProvider Provider, (long Size, DateTime WriteUtc)? Stamp)
    {
        /// <summary>Runs on paths that have nowhere to report a failure, and the file is lost either way.</summary>
        public void TryDispose()
        {
            try { Provider.Dispose(); } catch { /* still close the file */ }
            try { Stream.Dispose(); } catch { /* nothing useful to do */ }
        }
    }

    /// <summary>One track opened or opening ahead of its Load. All but the task is guarded by <see cref="_gate"/>.</summary>
    private sealed class PreparedChain(string path)
    {
        public string Path { get; } = path;
        public Task Opening { get; set; } = Task.CompletedTask;
        public Chain? Chain { get; set; }
        public bool Discarded { get; set; }
    }
}
