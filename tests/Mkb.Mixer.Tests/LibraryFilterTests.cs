using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LibraryFilterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-filter").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>a is 120 BPM, b is 128, c has not been analysed.</summary>
    private async Task<(MainViewModel Vm, FakeAudioEngine Engine, string[] Paths)> Library(bool analyse = false)
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b", "c");
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        store.SetAnalysis(paths[0], FakeAudioEngine.Analysis(120));
        store.SetAnalysis(paths[1], FakeAudioEngine.Analysis(128));
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true) { AnalyseLibraryBpm = analyse };
        await vm.BrowseFolderCommand.ExecuteAsync(_dir);
        return (vm, engine, paths);
    }

    private static string[] Titles(MainViewModel vm) => vm.BrowserRows.Select(r => r.Track.Title).Order().ToArray();

    [Fact]
    public async Task ARangeShowsOnlyTracksInsideItAndHidesUnknowns()
    {
        var (vm, _, _) = await Library();

        vm.BpmMin = 125; vm.BpmMax = 130;
        Assert.Equal(["b"], Titles(vm));

        vm.BpmMin = null; vm.BpmMax = null;
        Assert.Equal(["a", "b", "c"], Titles(vm));
    }

    [Fact]
    public async Task ABackwardsRangeStillFilters()
    {
        var (vm, _, _) = await Library();

        vm.BpmMin = 130; vm.BpmMax = 125;

        Assert.Equal(["b"], Titles(vm));
    }

    [Fact]
    public async Task OneEndOfTheRangeIsEnough()
    {
        var (vm, _, _) = await Library();

        vm.BpmMin = 125;

        Assert.Equal(["b"], Titles(vm));
    }

    [Fact]
    public async Task MatchUsesTheAudibleDecksBpm()
    {
        var (vm, engine, _) = await Library();
        engine.Analyses["/m/live.mp3"] = FakeAudioEngine.Analysis(125);
        await vm.DeckA.LoadAsync(new Track("/m/live.mp3", "live", Duration: TimeSpan.FromMinutes(4)));
        await vm.DeckA.PlayCommand.ExecuteAsync(null);
        vm.Tick(TimeSpan.Zero);

        vm.MatchBpmCommand.Execute(null);

        Assert.Equal(117.5m, vm.BpmMin);
        Assert.Equal(132.5m, vm.BpmMax);
    }

    [Fact]
    public async Task WithAnalysisOnTheFolderIsQueued()
    {
        var (vm, engine, paths) = await Library(analyse: true);

        await vm.Analysis.DrainAsync();

        Assert.Equal([paths[2]], engine.AnalysedPaths);   // a and b were cached
    }

    [Fact]
    public async Task WithAnalysisOffNothingIsQueued()
    {
        var (vm, _, _) = await Library(analyse: false);

        Assert.Equal(0, vm.Analysis.PendingCount);
    }

    [Fact]
    public async Task ATrackAppearsWhenItsAnalysisLandsInsideTheRange()
    {
        var (vm, engine, paths) = await Library(analyse: true);
        engine.Analyses[paths[2]] = FakeAudioEngine.Analysis(127);
        vm.BpmMin = 125; vm.BpmMax = 130;
        Assert.Equal(["b"], Titles(vm));

        await vm.Analysis.DrainAsync();

        Assert.Equal(["b", "c"], Titles(vm));
        Assert.Equal(127, vm.BrowserRows.Single(r => r.Track.Title == "c").Bpm);
    }

    [Fact]
    public async Task ASearchHonoursTheRangeAndHidesUnknowns()
    {
        string sub = Directory.CreateDirectory(Path.Combine(_dir, "sub")).FullName;
        string[] top = LibraryRowTests.Files(_dir, "t_in", "t_out", "t_unknown");
        string[] nested = LibraryRowTests.Files(sub, "n_in", "n_out");
        var store = TrackStore.InMemory();
        store.SetAnalysis(top[0], FakeAudioEngine.Analysis(127));
        store.SetAnalysis(top[1], FakeAudioEngine.Analysis(90));
        store.SetAnalysis(nested[0], FakeAudioEngine.Analysis(128));
        store.SetAnalysis(nested[1], FakeAudioEngine.Analysis(150));
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true) { AnalyseLibraryBpm = false, CurrentFolder = _dir };
        vm.BpmMin = 125; vm.BpmMax = 130;
        vm.SearchText = "_";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["n_in", "t_in"], Titles(vm));
    }

    [Fact]
    public void TheSettingDefaultsOnAwayFromPhones()
    {
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "fresh.json")));

        Assert.True(vm.AnalyseLibraryBpm);   // tests run with App.UsePhoneLayout false
    }

    [Fact]
    public async Task AFileNameOnlySearchHitSurvivesABpmRangeChange()
    {
        var (vm, _, _) = await Library();
        vm.SearchText = ".mp3";   // in every file name, in no title
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["a", "b", "c"], Titles(vm));

        vm.BpmMin = 115; vm.BpmMax = 125;

        Assert.Equal(["a"], Titles(vm));
    }

    [Fact]
    public async Task HalvingADecksBpmUpdatesItsLibraryRow()
    {
        var (vm, engine, paths) = await Library();
        engine.Analyses[paths[0]] = FakeAudioEngine.Analysis(120);
        await vm.DeckA.LoadAsync(vm.BrowserRows.Single(r => r.Track.Path == paths[0]).Track);

        vm.DeckA.HalveBpmCommand.Execute(null);

        Assert.Equal(60, vm.BrowserRows.Single(r => r.Track.Path == paths[0]).Bpm);
    }
}
