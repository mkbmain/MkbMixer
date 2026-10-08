using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class TrackStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-tracks").FullName;
    private string StorePath => Path.Combine(_dir, "tracks.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string AudioFile(string name = "a.mp3")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[100]);
        return path;
    }

    private static TrackAnalysis Analysed(double bpm) => FakeAudioEngine.Analysis(bpm, first: 1.5, last: 200);

    [Fact]
    public void RoundTripsAnalysisCuesAndHistory()
    {
        string track = AudioFile();
        var played = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        using (var store = new TrackStore(StorePath))
        {
            store.SetAnalysis(track, Analysed(128));
            store.Update(track, i => i.WithHotCue(2, 42.5));
            store.MarkPlayed(track, played);
        }

        using var reloaded = new TrackStore(StorePath);
        TrackInfo info = reloaded.Get(track)!;

        Assert.True(info.IsAnalysed);
        Assert.Equal(128, info.Bpm);
        Assert.Equal(1.5, info.FirstSoundSeconds);
        Assert.Equal(200, info.LastSoundSeconds);
        Assert.Equal([null, null, 42.5, null], info.HotCueSeconds);
        Assert.Equal(played, info.LastPlayedUtc);
        Assert.Equal(1, info.PlayCount);
    }

    [Fact]
    public void ACorruptFileLoadsEmpty()
    {
        File.WriteAllText(StorePath, "{ not json");

        using var store = new TrackStore(StorePath);

        Assert.Null(store.Get("/anything.mp3"));
    }

    [Fact]
    public void AChangedFileDropsItsAnalysisButKeepsCuesAndHistory()
    {
        string track = AudioFile();
        using var store = new TrackStore(StorePath);
        store.SetAnalysis(track, Analysed(128));
        store.Update(track, i => i.WithHotCue(0, 10));
        store.MarkPlayed(track, DateTime.UtcNow);

        File.AppendAllText(track, "re-tagged");
        TrackInfo info = store.Get(track)!;

        Assert.False(info.IsAnalysed);
        Assert.Null(info.Bpm);
        Assert.Equal(10, info.HotCueSeconds[0]);
        Assert.Equal(1, info.PlayCount);
    }

    [Fact]
    public void AnUnchangedFileKeepsItsAnalysis()
    {
        string track = AudioFile();
        using var store = new TrackStore(StorePath);
        store.SetAnalysis(track, Analysed(128));

        Assert.True(store.Get(track)!.IsAnalysed);
    }

    [Fact]
    public void ReanalysingKeepsTheUsersBpmCorrection()
    {
        string track = AudioFile();
        using var store = new TrackStore(StorePath);
        store.SetAnalysis(track, Analysed(80));
        store.Update(track, i => i with { BpmMultiplier = 2 });

        store.SetAnalysis(track, Analysed(80));

        Assert.Equal(160, store.Get(track)!.DisplayBpm);
    }

    [Fact]
    public void SavesWaitForTheDelayOrAFlush()
    {
        using var store = new TrackStore(StorePath, saveDelay: TimeSpan.FromHours(1));
        store.MarkPlayed(AudioFile(), DateTime.UtcNow);
        Assert.False(File.Exists(StorePath));

        store.Flush();

        Assert.True(File.Exists(StorePath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void RecentlyPlayedIsNewestFirstAndCapped()
    {
        using var store = TrackStore.InMemory();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++) store.MarkPlayed($"/m/{i}.mp3", t0.AddMinutes(i));
        store.Update("/m/never.mp3", i => i.WithHotCue(0, 1));

        Assert.Equal(["/m/4.mp3", "/m/3.mp3", "/m/2.mp3"], store.RecentlyPlayed(3));
    }

    [Fact]
    public void ANullEntryInTheFileIsSkippedNotFatal()
    {
        File.WriteAllText(StorePath, "{\"/a.mp3\": null, \"/b.mp3\": {\"PlayCount\": 2}}");

        using var store = new TrackStore(StorePath);

        Assert.Null(store.Get("/a.mp3"));
        Assert.Equal(2, store.Get("/b.mp3")!.PlayCount);
    }

    [Fact]
    public void FlushNeverThrowsWhenTheStoreCannotBeSerialised()
    {
        using var store = new TrackStore(StorePath);
        store.Update("/a.mp3", i => i with { Bpm = double.NaN });

        var ex = Record.Exception(store.Flush);

        Assert.Null(ex);
    }
}
