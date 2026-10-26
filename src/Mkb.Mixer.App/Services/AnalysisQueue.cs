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
/// every file on the UI thread. The last few results are kept whole, waveform and
/// all, so a prefetched track loads on its deck without a second decode, and a
/// deck load joins a decode the worker already has running for the same file.
/// </remarks>
public sealed class AnalysisQueue : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly Action<Action> _post;
    private readonly bool _manual;
    private readonly Lock _gate = new();
    // Where each pending path sits. Seq is when it was first queued, so a promoted
    // library track keeps its place among the next-up ones. A library queue entry
    // whose path has since been promoted, taken or dropped no longer matches here
    // and is skipped when reached, so nothing is ever searched for.
    private readonly Dictionary<string, (AnalysisPriority Priority, long Seq)> _pending = [];
    private readonly SortedSet<(long Seq, string Path)> _nextUp = [];
    private readonly Queue<(long Seq, string Path)> _library = new();
    private long _nextSeq;
    private readonly Dictionary<string, Task<TrackAnalysis>> _workerDecodes = [];
    private readonly Dictionary<string, Task<TrackAnalysis>> _deckDecodes = [];
    private readonly LinkedList<RecentResult> _recent = [];
    private readonly Dictionary<string, LinkedListNode<RecentResult>> _recentByPath = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _deckAnalyses = [];
    private Task? _worker;

    private sealed record RecentResult(string Path, FileStamp Stamp, TrackAnalysis Analysis);

    /// <summary>How many whole results, waveform included, are kept for deck loads. About 8 KB each.</summary>
    public const int RecentCapacity = 8;

    /// <summary>How long <see cref="Dispose"/> waits for a decode to notice it was cancelled.</summary>
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

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
        string path = track.Path;
        if (Recent(path) is { } recent)
        {
            // Normally in the store already; put it back if the store has forgotten it.
            if (Store.Get(path)?.IsAnalysed != true) Record(path, recent);
            return recent;
        }

        // Tied to disposal too, so shutting down stops the decode before the engine goes.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        Task<TrackAnalysis> analysis;
        Task<TrackAnalysis>? decode = null;
        lock (_gate)
        {
            // Waited on rather than joined: dropping this load must not cancel the
            // worker's decode, which records the result itself.
            if (_workerDecodes.TryGetValue(path, out Task<TrackAnalysis>? running))
                analysis = running.WaitAsync(linked.Token);
            else
                analysis = _deckDecodes[path] = decode = StartDecode(path, linked.Token);
            _deckAnalyses.Add(analysis);
        }
        try
        {
            TrackAnalysis result = await analysis;
            if (decode is not null && !linked.IsCancellationRequested) Record(path, result);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _deckAnalyses.Remove(analysis);
                if (decode is not null && _deckDecodes.GetValueOrDefault(path) == decode) _deckDecodes.Remove(path);
            }
        }
    }

    /// <summary>Queues a deck's next-up track ahead of any library work.</summary>
    public void Prefetch(Track? track)
    {
        if (track is null) return;
        string path = track.Path;
        lock (_gate)
        {
            if (!_pending.TryGetValue(path, out var entry))
                entry = (AnalysisPriority.Library, _nextSeq++);
            if (entry.Priority == AnalysisPriority.Library)
            {
                // Its old library queue entry goes stale: the priority no longer matches.
                _pending[path] = (AnalysisPriority.NextUp, entry.Seq);
                _nextUp.Add((entry.Seq, path));
            }
        }
        Wake();
    }

    /// <summary>Replaces the pending library work with these tracks. An empty list just clears it.</summary>
    public void QueueFolder(IEnumerable<Track> tracks)
    {
        List<string> paths = tracks.Select(t => t.Path).ToList();
        bool queued = false;
        lock (_gate)
        {
            foreach (var (seq, path) in _library)
                if (IsLive(path, AnalysisPriority.Library, seq)) _pending.Remove(path);
            _library.Clear();
            foreach (string path in paths)
            {
                if (!_pending.TryAdd(path, (AnalysisPriority.Library, _nextSeq))) continue;
                _library.Enqueue((_nextSeq++, path));
                queued = true;
            }
        }
        if (queued) Wake();
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
            if (_nextUp.Count > 0)
            {
                var first = _nextUp.Min;
                _nextUp.Remove(first);
                _pending.Remove(first.Path);
                path = first.Path;
                return true;
            }
            while (_library.TryDequeue(out var entry))
            {
                if (!IsLive(entry.Path, AnalysisPriority.Library, entry.Seq)) continue;
                _pending.Remove(entry.Path);
                path = entry.Path;
                return true;
            }
            path = null;
            return false;
        }
    }

    private bool IsLive(string path, AnalysisPriority priority, long seq) =>
        _pending.TryGetValue(path, out var entry) && entry == (priority, seq);

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        if (Store.Get(path)?.IsAnalysed == true) return;
        Task<TrackAnalysis>? deck;
        lock (_gate) deck = _deckDecodes.GetValueOrDefault(path);
        if (deck is not null)
        {
            // A deck is decoding it already and records it, unless that load is dropped.
            try { await deck.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* dropped or failed: the store says which */ }
            if (Store.Get(path)?.IsAnalysed == true) return;
        }

        Task<TrackAnalysis> decode;
        lock (_gate) decode = _workerDecodes[path] = StartDecode(path, ct);
        try
        {
            Record(path, await decode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // One undecodable file must not stop the rest of the folder.
        }
        finally
        {
            lock (_gate) _workerDecodes.Remove(path);
        }
    }

    // Started under the lock so a second request always finds it. Engines hand back
    // a task straight away; a synchronous throw becomes a faulted task like any other.
    private Task<TrackAnalysis> StartDecode(string path, CancellationToken ct)
    {
        try { return _engine.AnalyseAsync(path, ct); }
        catch (Exception e) { return Task.FromException<TrackAnalysis>(e); }
    }

    private void Record(string path, TrackAnalysis result)
    {
        // Empty means it could not be decoded at all, or there is no audio context.
        // Caching that would hide a BPM a later run could find.
        if (ReferenceEquals(result, TrackAnalysis.Empty)) return;
        Store.SetAnalysis(path, result);
        Remember(path, result);
        _post(() => Analysed?.Invoke(this, path));
    }

    private void Remember(string path, TrackAnalysis result)
    {
        FileStamp stamp = FileStamp.Of(path);
        // Nothing on disk to check it against later, so nothing to trust.
        if (stamp == FileStamp.Missing) return;
        lock (_gate)
        {
            if (_recentByPath.Remove(path, out var old)) _recent.Remove(old);
            _recentByPath[path] = _recent.AddFirst(new RecentResult(path, stamp, result));
            if (_recent.Count <= RecentCapacity) return;
            _recentByPath.Remove(_recent.Last!.Value.Path);
            _recent.RemoveLast();
        }
    }

    /// <summary>A kept result for the file as it is now, or null.</summary>
    private TrackAnalysis? Recent(string path)
    {
        LinkedListNode<RecentResult>? node;
        lock (_gate)
            if (!_recentByPath.TryGetValue(path, out node)) return null;
        // Statted outside the lock: a slow card must not hold up the worker.
        if (node.Value.Stamp != FileStamp.Of(path)) return null;
        lock (_gate)
        {
            if (node.List is null) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
        }
        return node.Value.Analysis;
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
