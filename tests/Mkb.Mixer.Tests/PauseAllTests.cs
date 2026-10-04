using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// The lock screen, headset buttons and audio focus pause and resume the whole mix,
/// not one deck, so the view model has to remember what it paused.
/// </summary>
public class PauseAllTests
{
    private static (MainViewModel Vm, FakeAudioEngine Engine) Playing(bool a, bool b)
    {
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        vm.DeckA.LoadAsync(new Track("/m/a.mp3", "A", Duration: TimeSpan.FromMinutes(3))).Wait();
        vm.DeckB.LoadAsync(new Track("/m/b.mp3", "B", Duration: TimeSpan.FromMinutes(3))).Wait();
        if (a) vm.DeckA.PlayCommand.Execute(null);
        if (b) vm.DeckB.PlayCommand.Execute(null);
        return (vm, engine);
    }

    [Fact]
    public void PauseAllPausesEveryPlayingDeck()
    {
        var (vm, engine) = Playing(a: true, b: true);

        Assert.True(vm.PauseAll());

        Assert.Equal(PlaybackState.Paused, engine.A.State);
        Assert.Equal(PlaybackState.Paused, engine.B.State);
    }

    [Fact]
    public void PauseAllReportsWhenNothingWasPlaying()
    {
        var (vm, _) = Playing(a: false, b: false);
        Assert.False(vm.PauseAll());
    }

    [Fact]
    public void ResumeRestartsOnlyTheDecksThatWerePaused()
    {
        var (vm, engine) = Playing(a: true, b: false);
        vm.PauseAll();

        vm.ResumePaused();

        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Assert.Equal(PlaybackState.Stopped, engine.B.State);
    }

    [Fact]
    public void ASecondPauseWithNothingPlayingKeepsTheFirstPausesMemory()
    {
        // e.g. the lock screen pauses, then a phone call takes focus.
        var (vm, engine) = Playing(a: false, b: true);
        vm.PauseAll();
        vm.PauseAll();

        vm.ResumePaused();

        Assert.Equal(PlaybackState.Playing, engine.B.State);
    }

    [Fact]
    public void ResumeDoesNothingExtraWhenTheUserAlreadyRestartedADeck()
    {
        var (vm, engine) = Playing(a: true, b: false);
        vm.CrossfaderPosition = 1f;              // B in front
        vm.PauseAll();
        vm.DeckA.PlayCommand.Execute(null);      // user resumes A by hand

        vm.ResumePaused();

        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Assert.Equal(PlaybackState.Stopped, engine.B.State);
    }

    [Fact]
    public void ResumeWithNothingRememberedPlaysTheDeckInFront()
    {
        var (vm, engine) = Playing(a: false, b: false);
        vm.CrossfaderPosition = 0.9f;

        vm.ResumePaused();

        Assert.Equal(PlaybackState.Playing, engine.B.State);
        Assert.Equal(PlaybackState.Stopped, engine.A.State);
    }
}
