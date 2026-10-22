using Avalonia.Threading;
using Mkb.Mixer.Library;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class AutoCueVmTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-autocue").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(4));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFadedOutDeckDoesNotStartItsNextTrack(bool endsAfterTheFade) => AvaloniaTest.Run(() =>
    {
        // The audio thread reports the end, and the deck handles it on a later UI
        // job: either before or after the tick that finishes the fade.
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(_dir, "s.json")))
        {
            AutoCueEnabled = true,
            CrossfadeSeconds = 20,
            CrossfaderPosition = Crossfader.DeckAOnly
        };
        vm.DeckA.Playlist.Add(T("a2"));
        vm.DeckB.Playlist.Add(T("b1"));
        engine.A.Load(T("a1"));
        engine.A.Play();
        engine.A.SetRemaining(TimeSpan.FromSeconds(5));

        vm.Tick(TimeSpan.Zero);
        Assert.Equal(PlaybackState.Playing, engine.B.State);

        if (endsAfterTheFade) vm.Tick(TimeSpan.FromSeconds(5.1));
        engine.A.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
        vm.Tick(TimeSpan.FromSeconds(5.2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a1", engine.A.Track?.Title);
        Assert.NotEqual(PlaybackState.Playing, engine.A.State);
        Assert.Equal(["a2"], vm.DeckA.Playlist.Select(t => t.Title));
    });

    [Fact]
    public void AnEndWithNoFadeStillStartsTheNextTrack() => AvaloniaTest.Run(() =>
    {
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(_dir, "s.json")))
        {
            AutoCueEnabled = false
        };
        vm.DeckA.Playlist.Add(T("a2"));
        engine.A.Load(T("a1"));
        engine.A.Play();

        engine.A.RaiseTrackEnded();
        for (int i = 0; i < 20 && engine.A.Track?.Title != "a2"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.Equal("a2", engine.A.Track?.Title);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
    });
}
