using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;
using static Mkb.Mixer.Tests.AutoCueTimingTests;

namespace Mkb.Mixer.Tests;

public class TempoMatchTests
{
    /// <summary>Outgoing deck A at <paramref name="outBpm"/>, B about to bring in a track at <paramref name="inBpm"/>.</summary>
    private static (AutoCue Cue, FakeDeck A, FakeDeck B) Transition(TempoMatchMode mode, double? outBpm, double? inBpm)
    {
        var (cue, a, b, timings) = Setup();
        timings[Current.Path] = new TrackTiming(null, null, outBpm);
        timings[Next.Path] = new TrackTiming(null, null, inBpm);
        cue.TempoMatch = mode;
        a.SetRemaining(TimeSpan.FromSeconds(20));
        return (cue, a, b);
    }

    [Fact]
    public void OffLeavesTheIncomingTempoAlone()
    {
        var (cue, _, b) = Transition(TempoMatchMode.Off, 124, 120);
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(1f, b.Tempo);
    }

    [Fact]
    public void MatchBringsTheIncomingTrackToTheOutgoingBpm()
    {
        var (cue, _, b) = Transition(TempoMatchMode.Match, 124, 120);
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(124.0 / 120, b.Tempo, 4);
    }

    [Fact]
    public void MatchAllowsForTheOutgoingDecksTempo()
    {
        var (cue, a, b) = Transition(TempoMatchMode.Match, 120, 120);
        a.Tempo = 1.04f;
        a.SetRemaining(TimeSpan.FromSeconds(20));
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(1.04, b.Tempo, 4);
    }

    [Theory]
    [InlineData(120.0, 140.0)]
    [InlineData(120.0, null)]
    [InlineData(null, 120.0)]
    public void ATempoTooFarApartOrUnknownIsLeftAlone(double? outBpm, double? inBpm)
    {
        var (cue, _, b) = Transition(TempoMatchMode.Match, outBpm, inBpm);
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(1f, b.Tempo);
    }

    [Fact]
    public void GlideEasesBackToNormalAfterTheFade()
    {
        var (cue, _, b) = Transition(TempoMatchMode.MatchAndGlide, 126, 120);
        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromSeconds(20));   // fade complete
        Assert.Equal(1.05, b.Tempo, 4);

        cue.Tick(TimeSpan.FromSeconds(24));
        Assert.Equal(1.025, b.Tempo, 4);

        cue.Tick(TimeSpan.FromSeconds(28));
        cue.Tick(TimeSpan.FromSeconds(30));
        Assert.Equal(1f, b.Tempo);
    }

    [Fact]
    public void MatchAloneDoesNotGlide()
    {
        var (cue, _, b) = Transition(TempoMatchMode.Match, 126, 120);
        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromSeconds(20));
        cue.Tick(TimeSpan.FromSeconds(40));
        Assert.Equal(1.05, b.Tempo, 4);
    }

    [Fact]
    public void ChangingTheTempoByHandStopsTheGlide()
    {
        var (cue, _, b) = Transition(TempoMatchMode.MatchAndGlide, 126, 120);
        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromSeconds(20));
        cue.Tick(TimeSpan.FromSeconds(22));

        b.Tempo = 1.1f;
        cue.CancelGlide(DeckId.B);
        cue.Tick(TimeSpan.FromSeconds(28));

        Assert.Equal(1.1f, b.Tempo);
    }

    [Fact]
    public void TheDeckFollowsATempoTheAutoCueSetWithoutCountingItAsTheUsers()
    {
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        int userChanges = 0;
        deck.UserChangedTempo += (_, _) => userChanges++;

        fake.Tempo = 1.05f;
        deck.Refresh();
        Assert.Equal(1.05, deck.Tempo, 4);
        Assert.Equal(0, userChanges);

        deck.Tempo = 0.9;
        Assert.Equal(1, userChanges);
        Assert.Equal(0.9f, fake.Tempo);
    }

    [Fact]
    public void TheModeIsRemembered()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path)) { TempoMatchIndex = 2 };
        vm.SaveState();

        var reloaded = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));

        Assert.Equal(TempoMatchMode.MatchAndGlide, reloaded.TempoMatch);
        Assert.Equal(2, reloaded.TempoMatchIndex);
    }
}
