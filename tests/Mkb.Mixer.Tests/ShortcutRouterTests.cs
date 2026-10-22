using Avalonia.Input;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>Key events reach the same deck and mixer code the on-screen controls use.</summary>
public class ShortcutRouterTests
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;

    private static (MainViewModel Vm, FakeAudioEngine Engine) Mixer(bool load = true)
    {
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        if (load)
        {
            vm.DeckA.LoadAsync(new Track("/m/a.mp3", "A", Duration: TimeSpan.FromMinutes(3))).Wait();
            vm.DeckB.LoadAsync(new Track("/m/b.mp3", "B", Duration: TimeSpan.FromMinutes(3))).Wait();
        }
        return (vm, engine);
    }

    /// <summary>A full press and release, so the next press is not an auto-repeat.</summary>
    private static bool Tap(MainViewModel vm, Key key, KeyModifiers mods = Ctrl, bool text = false)
    {
        bool handled = vm.ShortcutKeys.KeyDown(key, mods, text);
        vm.ShortcutKeys.KeyUp(key);
        return handled;
    }

    [Fact]
    public void PlayPauseTogglesTheRightDeck()
    {
        var (vm, engine) = Mixer();

        Tap(vm, Key.Q);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Tap(vm, Key.Q);
        Assert.Equal(PlaybackState.Paused, engine.A.State);
        Tap(vm, Key.P);
        Assert.Equal(PlaybackState.Playing, engine.B.State);
    }

    [Fact]
    public void HotCueSetsThenJumps()
    {
        var (vm, _) = Mixer();

        Tap(vm, Key.D1);
        Assert.True(vm.DeckA.HotCues[0].IsSet);
        vm.DeckA.HotCues[0].Seconds = 42;
        Tap(vm, Key.D1);
        Assert.Equal(42, vm.DeckA.HotCues[0].Seconds);
    }

    [Fact]
    public void ShiftHotCueClears()
    {
        var (vm, _) = Mixer();
        Tap(vm, Key.D2);
        Assert.True(vm.DeckA.HotCues[1].IsSet);

        Tap(vm, Key.D2, Ctrl | KeyModifiers.Shift);

        Assert.False(vm.DeckA.HotCues[1].IsSet);
    }

    [Fact]
    public void CrossfaderStepsClampAndCentre()
    {
        var (vm, _) = Mixer();
        vm.CrossfaderPosition = 0.05f;
        Tap(vm, Key.D5);
        Assert.Equal(0f, vm.CrossfaderPosition);

        for (var i = 0; i < 10; i++) Tap(vm, Key.D6);
        Assert.Equal(1f, vm.CrossfaderPosition);

        Tap(vm, Key.T);
        Assert.Equal(0.5f, vm.CrossfaderPosition);
    }

    [Fact]
    public void AutoCueToggles()
    {
        var (vm, _) = Mixer();
        bool before = vm.AutoCueEnabled;
        Tap(vm, Key.U);
        Assert.Equal(!before, vm.AutoCueEnabled);
        Tap(vm, Key.U);
        Assert.Equal(before, vm.AutoCueEnabled);
    }

    [Fact]
    public void PauseAllTogglesWholeMix()
    {
        var (vm, engine) = Mixer();
        vm.DeckA.PlayCommand.Execute(null);
        vm.DeckB.PlayCommand.Execute(null);

        Tap(vm, Key.B);
        Assert.Equal(PlaybackState.Paused, engine.A.State);
        Assert.Equal(PlaybackState.Paused, engine.B.State);

        Tap(vm, Key.B);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Assert.Equal(PlaybackState.Playing, engine.B.State);
    }

    [Fact]
    public void AddSelectedAddsLibrarySelectionToDeck()
    {
        var (vm, _) = Mixer(load: false);
        var track = new Track("/m/x.mp3", "X", Duration: TimeSpan.FromMinutes(3));
        vm.Library.SelectedBrowserRow = new LibraryRow(track);

        Tap(vm, Key.M);

        Assert.Contains(track, vm.DeckB.Playlist);
        Assert.Empty(vm.DeckA.Playlist);
    }

    [Fact]
    public void FocusSearchRaisesEvent()
    {
        var (vm, _) = Mixer();
        var raised = 0;
        vm.FocusSearchRequested += (_, _) => raised++;

        Assert.True(Tap(vm, Key.F));
        Assert.True(Tap(vm, Key.F, text: true));

        Assert.Equal(2, raised);
    }

    [Fact]
    public void NothingElseFiresInTextEntry()
    {
        var (vm, engine) = Mixer();

        Assert.False(Tap(vm, Key.Q, text: true));

        Assert.Equal(PlaybackState.Stopped, engine.A.State);
    }

    [Fact]
    public void NothingFiresWhileEditorOpen()
    {
        var (vm, engine) = Mixer();
        vm.IsShortcutEditorOpen = true;

        Assert.False(Tap(vm, Key.Q));
        Assert.Equal(PlaybackState.Stopped, engine.A.State);

        Assert.True(Tap(vm, Key.OemComma));
        Assert.False(vm.IsShortcutEditorOpen);
    }

    [Fact]
    public void EditorShortcutOpensEditor()
    {
        var (vm, _) = Mixer();
        Tap(vm, Key.OemComma);
        Assert.True(vm.IsShortcutEditorOpen);
    }

    [Fact]
    public void NudgeHoldsUntilKeyUpAndIgnoresRepeats()
    {
        var (vm, _) = Mixer();

        vm.ShortcutKeys.KeyDown(Key.A, Ctrl, false);
        Assert.Equal(0.96, vm.DeckA.Tempo, 6);
        vm.ShortcutKeys.KeyDown(Key.A, Ctrl, false);
        Assert.Equal(0.96, vm.DeckA.Tempo, 6);
        vm.ShortcutKeys.KeyUp(Key.A);

        Assert.Equal(1.0, vm.DeckA.Tempo, 6);
    }

    [Fact]
    public void NudgeEndsWhenModifierReleasedFirst()
    {
        var (vm, _) = Mixer();

        vm.ShortcutKeys.KeyDown(Key.A, Ctrl, false);
        vm.ShortcutKeys.KeyUp(Key.LeftCtrl);
        Assert.Equal(0.96, vm.DeckA.Tempo, 6);
        vm.ShortcutKeys.KeyUp(Key.A);

        Assert.Equal(1.0, vm.DeckA.Tempo, 6);
    }

    [Fact]
    public void ReleaseAllEndsHeldNudge()
    {
        var (vm, _) = Mixer();
        vm.ShortcutKeys.KeyDown(Key.S, Ctrl, false);
        Assert.Equal(1.04, vm.DeckA.Tempo, 6);

        vm.ShortcutKeys.ReleaseAll();

        Assert.Equal(1.0, vm.DeckA.Tempo, 6);
    }

    [Fact]
    public void UnavailableActionsAreNoOps()
    {
        var (vm, engine) = Mixer(load: false);
        vm.CueMode = CueMode.Off;

        var ex = Record.Exception(() =>
        {
            Tap(vm, Key.D1);
            Tap(vm, Key.D1, Ctrl | KeyModifiers.Shift);
            Tap(vm, Key.E);
            Tap(vm, Key.W);
        });

        Assert.Null(ex);
        Assert.False(vm.DeckA.IsCued);
        Assert.False(vm.DeckA.HotCues[0].IsSet);
    }

    [Fact]
    public void UnboundKeyIsNotHandled()
    {
        var (vm, _) = Mixer();
        Assert.False(Tap(vm, Key.Q, KeyModifiers.None));
    }

    /// <summary>A renamed catalogue id would otherwise match a key and silently do nothing.</summary>
    [Fact]
    public void EveryCatalogueActionIsHandledByTheRouter()
    {
        foreach (var action in Mkb.Mixer.App.Shortcuts.ShortcutCatalog.ForThisPlatform)
        {
            var (vm, _) = Mixer();
            var g = action.Default!;
            Assert.True(Tap(vm, g.Key, g.KeyModifiers), $"{action.Id} has no handler in ShortcutRouter");
            vm.ShortcutKeys.ReleaseAll();
        }
    }
}
