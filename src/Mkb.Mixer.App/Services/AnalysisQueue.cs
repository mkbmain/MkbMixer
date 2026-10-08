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
    private readonly Lock _gate = new();
    private readonly List<(string Path, AnalysisPriority Priority)> _pending = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    /// <param name="post">
    /// Runs an action on the UI thread. Defaults to the creating thread's
    /// synchronisation context, or runs inline when there is none, as in tests.
    /// </param>
    /// <param name="manual">No worker: the caller runs <see cref="DrainAsync"/>. For tests.</param>
    public AnalysisQueue(IAudioEngine engine, TrackStore store, Action<Action>? post = null, bool manual = false)
    {
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
        TrackAnalysis result = await _engine.AnalyseAsync(track.Path, ct);
        if (!ct.IsCancellationRequested) Record(track.Path, result);
        return result;
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
            foreach (string path in paths)
                if (!_pending.Exists(p => p.Path == path))
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
            Record(path, await _engine.AnalyseAsync(path, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // One undecodable file must not stop the rest of the folder.
        }
    }

    private void Record(string path, TrackAnalysis result)
    {
        // Empty means it could not be decoded at all, or there is no audio context.
        // Caching that would hide a BPM a later run could find.
        if (ReferenceEquals(result, TrackAnalysis.Empty)) return;
        Store.SetAnalysis(path, result);
        _post(() => Analysed?.Invoke(this, path));
    }

    public void Dispose() => _stop.Cancel();
}
