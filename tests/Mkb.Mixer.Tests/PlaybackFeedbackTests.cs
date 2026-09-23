using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Playback that cannot happen must say so. Both of these failed silently before:
/// pressing play with no audio device, or with nothing loaded, simply did nothing.
/// </summary>
public class PlaybackFeedbackTests
{
    private static MainViewModel NewVm(FakeAudioEngine engine) =>
        new(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));

    [Fact]
    public void PressingPlayWithNoAudioOutputExplainsWhy()
    {
        var engine = new FakeAudioEngine { IsOutputAvailable = false, OutputError = "no backend" };
        var vm = NewVm(engine);

        vm.DeckA.PlayCommand.Execute(null);

        Assert.Contains("no audio output", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlaybackState.Stopped, engine.A.State);
    }

    [Fact]
    public void PressingPlayWithNothingLoadedExplainsWhy()
    {
        var engine = new FakeAudioEngine();
        var vm = NewVm(engine);

        vm.DeckA.PlayCommand.Execute(null);

        Assert.Contains("nothing loaded", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PressingPlayWithAQueuedTrackStartsIt()
    {
        var engine = new FakeAudioEngine();
        var vm = NewVm(engine);
        vm.DeckA.Playlist.Add(new Track("/m/a.mp3", "A", "Artist", Duration: TimeSpan.FromMinutes(3)));

        vm.DeckA.PlayCommand.Execute(null);

        Assert.Equal(PlaybackState.Playing, engine.A.State);
    }

    [Fact]
    public void TheAudioRouteIsReportedWhenOutputWorks()
    {
        var vm = NewVm(new FakeAudioEngine());
        Assert.Contains("test device", vm.AudioStatus);
    }

    [Fact]
    public void TheAudioRouteIsReportedWhenOutputIsMissing()
    {
        var vm = NewVm(new FakeAudioEngine { IsOutputAvailable = false, OutputError = "no backend" });
        Assert.Contains("no audio output", vm.AudioStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAudioRouteSurvivesAFolderLoadOverwritingTheStatusLine()
    {
        // The original failure mode: the "no audio output" warning was written to
        // StatusMessage and immediately replaced by "Reading <folder>…", so the
        // user never saw it. AudioStatus is a separate, persistent field.
        var vm = NewVm(new FakeAudioEngine { IsOutputAvailable = false, OutputError = "no backend" });
        vm.StatusMessage = "Reading /home/user/Music…";

        Assert.Contains("no audio output", vm.AudioStatus, StringComparison.OrdinalIgnoreCase);
    }
}
