using Mkb.Mixer.App.Services;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public sealed class WaveformCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkbmixer-wf-" + Guid.NewGuid().ToString("N"));

    public WaveformCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string CacheDir => Path.Combine(_dir, "waveforms");

    private string AudioFile(string name, string content = "x")
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static readonly FileStamp Stamp = new(1234, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private string OnlyCacheFile() => Directory.GetFiles(CacheDir).Single(f => !f.EndsWith(".tmp"));

    [Fact]
    public void RoundTripsWithinQuantisation()
    {
        var cache = new WaveformCache(CacheDir);
        Waveform wf = FakeAudioEngine.Analysis().Waveform;

        cache.Save("/m/a.mp3", Stamp, wf);
        Waveform? loaded = cache.TryLoad("/m/a.mp3", Stamp);

        Assert.NotNull(loaded);
        Assert.Equal(wf.Peaks.Length, loaded.Peaks.Length);
        for (int i = 0; i < wf.Peaks.Length; i++)
            Assert.InRange(Math.Abs(wf.Peaks[i] - loaded.Peaks[i]), 0f, 1f / 255f);
    }

    [Fact]
    public void MissingFileIsAMiss()
    {
        Assert.Null(new WaveformCache(CacheDir).TryLoad("/m/none.mp3", Stamp));
    }

    [Fact]
    public void ChangedStampIsAMiss()
    {
        var cache = new WaveformCache(CacheDir);
        cache.Save("/m/a.mp3", Stamp, Waveform.Empty);

        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp with { Size = 99 }));
        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp with { WriteUtc = Stamp.WriteUtc.AddTicks(1) }));
    }

    [Fact]
    public void TruncatedFileIsAMiss()
    {
        var cache = new WaveformCache(CacheDir);
        cache.Save("/m/a.mp3", Stamp, FakeAudioEngine.Analysis().Waveform);
        string file = OnlyCacheFile();
        byte[] bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length - 10)]);

        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp));
    }

    [Fact]
    public void CorruptMagicIsAMiss()
    {
        var cache = new WaveformCache(CacheDir);
        cache.Save("/m/a.mp3", Stamp, FakeAudioEngine.Analysis().Waveform);
        string file = OnlyCacheFile();
        byte[] bytes = File.ReadAllBytes(file);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp));
    }

    [Fact]
    public void SaveToAnUnwritableLocationDoesNotThrow()
    {
        string blocker = AudioFile("blocker");   // a file where the directory should be
        var cache = new WaveformCache(Path.Combine(blocker, "sub"));

        cache.Save("/m/a.mp3", Stamp, Waveform.Empty);

        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp));
    }

    [Fact]
    public void ForgetDeletesTheFileAndToleratesNone()
    {
        var cache = new WaveformCache(CacheDir);
        cache.Save("/m/a.mp3", Stamp, Waveform.Empty);

        cache.Forget("/m/a.mp3");
        cache.Forget("/m/never.mp3");

        Assert.Null(cache.TryLoad("/m/a.mp3", Stamp));
    }

    [Fact]
    public async Task SecondDeckAnalysisUsesTheCacheAndLeavesTheStoreAlone()
    {
        string audio = AudioFile("a.mp3");
        string storePath = Path.Combine(_dir, "tracks.json");
        var engine = new FakeAudioEngine();
        engine.Analyses[audio] = FakeAudioEngine.Analysis(bpm: 120, first: 1.5, last: 200);
        using var store = new TrackStore(storePath);
        using var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true, waveforms: new WaveformCache(CacheDir));
        var track = new Track(audio, "a", Duration: TimeSpan.FromMinutes(3));

        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);
        // The cache is written off the calling thread, so wait for it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (new WaveformCache(CacheDir).TryLoad(audio, FileStamp.Of(audio)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        store.Flush();
        File.Delete(storePath);
        var analysed = new List<string>();
        queue.Analysed += (_, p) => analysed.Add(p);

        TrackAnalysis second = await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);
        store.Flush();

        Assert.Single(engine.AnalysedPaths);
        Assert.False(File.Exists(storePath), "the store was written again");
        Assert.Empty(analysed);
        Assert.Equal(120, second.Bpm);
        Assert.Equal(TimeSpan.FromSeconds(1.5), second.FirstSound);
        Assert.Equal(TimeSpan.FromSeconds(200), second.LastSound);
        Assert.Equal(2000, second.Waveform.Peaks.Length);
    }

    [Fact]
    public async Task ChangedFileIsDecodedAgain()
    {
        string audio = AudioFile("a.mp3");
        var engine = new FakeAudioEngine();
        using var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true, waveforms: new WaveformCache(CacheDir));
        var track = new Track(audio, "a", Duration: TimeSpan.FromMinutes(3));
        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (new WaveformCache(CacheDir).TryLoad(audio, FileStamp.Of(audio)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        File.WriteAllText(audio, "much longer content now");
        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.AnalysedPaths.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileEditedDuringTheDecodeIsDecodedAgainNextTime(bool withCache)
    {
        string audio = AudioFile("a.mp3");
        var engine = new FakeAudioEngine();
        engine.Held.Add(audio);
        using var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true,
            waveforms: withCache ? new WaveformCache(CacheDir) : null);
        var track = new Track(audio, "a", Duration: TimeSpan.FromMinutes(3));

        Task<TrackAnalysis> first = queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (engine.AnalysedPaths.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the decode never started");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        File.WriteAllText(audio, "edited while decoding");
        engine.ReleaseHeld();
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // let the cache save land
        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.AnalysedPaths.Count);
    }

    [Fact]
    public async Task BackgroundAnalysisFillsTheCache()
    {
        string audio = AudioFile("a.mp3");
        var engine = new FakeAudioEngine();
        using var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true, waveforms: new WaveformCache(CacheDir));
        queue.QueueFolder([new Track(audio, "a", Duration: TimeSpan.FromMinutes(3))]);

        await queue.DrainAsync();
        // Saved off the UI thread, so give it a moment.
        var cache = new WaveformCache(CacheDir);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (cache.TryLoad(audio, FileStamp.Of(audio)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.NotNull(cache.TryLoad(audio, FileStamp.Of(audio)));
    }

    [Fact]
    public async Task WithoutACacheEveryDeckLoadDecodes()
    {
        string audio = AudioFile("a.mp3");
        var engine = new FakeAudioEngine();
        using var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true);
        var track = new Track(audio, "a", Duration: TimeSpan.FromMinutes(3));

        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);
        await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.AnalysedPaths.Count);
    }

    [Fact]
    public void ForgetMissingReportsEachForgottenPath()
    {
        string deleted = AudioFile("gone.mp3");
        using var store = new TrackStore(Path.Combine(_dir, "t.json"));
        store.MarkPlayed(deleted, DateTime.UtcNow);
        File.Delete(deleted);
        var seen = new List<string>();

        store.ForgetMissing(seen.Add);

        Assert.Equal([deleted], seen);
    }
}
