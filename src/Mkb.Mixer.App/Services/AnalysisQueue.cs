using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.App.Services;

/// <summary>Which background analysis runs first. Lower runs sooner.</summary>
public enum AnalysisPriority { NextUp = 0, Library = 1 }

/// <summary>
/// Analyses tracks for tempo and start/end points and records the results in the
/// <see cref="TrackStore"/>.
/// </summary>
/// <remarks>
/// A deck load is analysed straight away, beside everything else: it needs the
/// waveform too and must never wait behind a folder of background work. The rest
/// goes through one worker, highest priority first, so background analysis never
/// competes with itself for the CPU. The worker checks the cache when it reaches a
/// track rather than when it is queued, so opening a big folder does not stat
/// every file on the UI thread.
/// </remarks>
public sealed class AnalysisQueue : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly Action<Action> _post;
    private readonly bool _manual;
    private readonly WaveformCache? _waveforms;
    private readonly Lock _gate = new();
    private readonly List<(string Path, AnalysisPriority Priority)> _pending = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _deckAnalyses = [];
    private Task? _worker;

    /// <summary>How long <see cref="Dispose"/> waits for a decode to notice it was cancelled.</summary>
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    /// <param name="post">
    /// Runs an action on the UI thread. Defaults to the creating thread's
    /// synchronisation context, or runs inline when there is none, as in tests.
    /// </param>
    /// <param name="manual">No worker: the caller runs <see cref="DrainAsync"/>. For tests.</param>
    /// <param name="waveforms">Where waveforms are kept so an analysed track need not be decoded again. Null keeps none.</param>
    public AnalysisQueue(IAudioEngine engine, TrackStore store, Action<Action>? post = null, bool manual = false,
                         WaveformCache? waveforms = null)
    {
        _waveforms = waveforms;
        _engine = engine;
        Store = store;
        _manual = manual;
        SynchronizationContext? ui = SynchronizationContext.Current;
        _post = post ?? (ui is null ? a => a() : a => ui.Post(_ => a(), null));
    }

    public TrackStore Store { get; }

    /// <summary>Raised on the UI thread with the path of a track whose analysis just landed in <see cref="Store"/>.</summary>
    public event EventHandler<string>? Analysed;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Analyses a track that has just been loaded on a deck, cached or not.</summary>
    public async Task<TrackAnalysis> AnalyseForDeckAsync(Track track, CancellationToken ct = default)
    {
        // Tied to disposal too, so shutting down stops the decode before the engine goes.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        Task<(TrackAnalysis Result, FileStamp Stamp, bool Cached)> analysis = AnalyseOrCachedAsync(track.Path, linked.Token);
        lock (_gate) _deckAnalyses.Add(analysis);
        try
        {
            (TrackAnalysis result, FileStamp stamp, bool cached) = await analysis;
            if (!cached && !linked.IsCancellationRequested) Record(track.Path, result, stamp);
            return result;
        }
        finally
        {
            lock (_gate) _deckAnalyses.Remove(analysis);
        }
    }

    private async Task<(TrackAnalysis Result, FileStamp Stamp, bool Cached)> AnalyseOrCachedAsync(string path, CancellationToken ct)
    {
        // ConfigureAwait(false) throughout: this is the task Dispose waits for on the UI
        // thread, so it must not need that thread to finish. Its caller still resumes there.
        // On the pool: this stats the file and reads the cache, which must not happen on the UI thread.
        // The stamp is taken before the decode, so a file edited during it mismatches next time
        // instead of keeping an old waveform under its new stamp.
        (FileStamp stamp, TrackAnalysis? hit) = await Task.Run(
            () => (FileStamp.Of(path), _waveforms is null ? null : FromCache(path)), CancellationToken.None)
            .ConfigureAwait(false);
        // A superseded load must not hand back an old waveform.
        ct.ThrowIfCancellationRequested();
        if (hit is not null) return (hit, stamp, true);
        return (await _engine.AnalyseAsync(path, ct).ConfigureAwait(false), stamp, false);
    }

    private TrackAnalysis? FromCache(string path)
    {
        if (Store.Get(path) is not { IsAnalysed: true } info) return null;
        Waveform? waveform = _waveforms!.TryLoad(path, info.Stamp);
        if (waveform is null) return null;
        return new TrackAnalysis(waveform, info.Bpm,
            info.BeatOffsetSeconds is { } b ? TimeSpan.FromSeconds(b) : null,
            info.FirstSoundSeconds is { } f ? TimeSpan.FromSeconds(f) : null,
            info.LastSoundSeconds is { } l ? TimeSpan.FromSeconds(l) : null);
    }

    /// <summary>Queues a deck's next-up track ahead of any library work.</summary>
    public void Prefetch(Track? track)
    {
        if (track is null) return;
        lock (_gate)
        {
            int i = _pending.FindIndex(p => p.Path == track.Path);
            if (i >= 0) _pending[i] = (track.Path, AnalysisPriority.NextUp);
            else _pending.Add((track.Path, AnalysisPriority.NextUp));
        }
        Wake();
    }

    /// <summary>Replaces the pending library work with these tracks. An empty list just clears it.</summary>
    public void QueueFolder(IEnumerable<Track> tracks)
    {
        List<string> paths = tracks.Select(t => t.Path).ToList();
        lock (_gate)
        {
            _pending.RemoveAll(p => p.Priority == AnalysisPriority.Library);
            // A set, not _pending.Exists per path: a big folder would be quadratic, on the UI thread.
            HashSet<string> queued = [.. _pending.Select(p => p.Path)];
            foreach (string path in paths)
                if (queued.Add(path))
                    _pending.Add((path, AnalysisPriority.Library));
        }
        Wake();
    }

    /// <summary>Runs everything pending, in priority order, on the calling thread. For manual mode.</summary>
    public async Task DrainAsync()
    {
        while (TryTake(out string? path))
            await ProcessAsync(path, CancellationToken.None);
    }

    private void Wake()
    {
        if (_manual) return;
        lock (_gate) _worker ??= Task.Run(RunAsync);
        _wake.Release();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_stop.Token);
                while (TryTake(out string? path))
                    await ProcessAsync(path, _stop.Token);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    private bool TryTake([NotNullWhen(true)] out string? path)
    {
        lock (_gate)
        {
            if (_pending.Count == 0) { path = null; return false; }
            int best = 0;
            for (int i = 1; i < _pending.Count; i++)
                if (_pending[i].Priority < _pending[best].Priority) best = i;
            path = _pending[best].Path;
            _pending.RemoveAt(best);
            return true;
        }
    }

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        if (Store.Get(path)?.IsAnalysed == true) return;
        try
        {
            // Before the decode; see AnalyseOrCachedAsync. The worker is off the UI thread.
            FileStamp stamp = FileStamp.Of(path);
            Record(path, await _engine.AnalyseAsync(path, ct), stamp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // One undecodable file must not stop the rest of the folder.
        }
    }

    private void Record(string path, TrackAnalysis result, FileStamp stamp)
    {
        // Empty means it could not be decoded at all, or there is no audio context.
        // Caching that would hide a BPM a later run could find.
        if (ReferenceEquals(result, TrackAnalysis.Empty)) return;
        Store.SetAnalysis(path, result, stamp);
        if (_waveforms is not null)
        {
            // Off the UI thread: Record can run there, and this is a file write.
            WaveformCache cache = _waveforms;
            _ = Task.Run(() => cache.Save(path, stamp, result.Waveform));
        }
        _post(() => Analysed?.Invoke(this, path));
    }

    /// <summary>
    /// Cancels all analysis and waits briefly for it to stop. The owner disposes the
    /// audio engine next, and a decode still running against it would use freed
    /// native memory.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        Task[] running;
        lock (_gate)
            running = [.. _deckAnalyses, .. _worker is null ? [] : new[] { _worker }];
        try { Task.WaitAll(running, DisposeTimeout); }
        catch (AggregateException) { /* cancelled or failed: either way, stopped */ }
    }
}
