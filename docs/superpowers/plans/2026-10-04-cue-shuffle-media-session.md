# Headphone cue, shuffle/repeat, Android media session — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-deck shuffle/repeat, a headphone cue (Split or second-device), and Android media-session controls with audio focus handling.

**Architecture:** Shuffle/repeat changes only `DeckViewModel.DequeueNext()`. The cue taps each deck's SoundFlow player with a modifier (pre-fader) into a `CueBus`; a modifier on the master mixer either splits the master output L/R (Split) or feeds a lock-free ring buffer read by a second device (Device). Pause/resume-all lives in `MainViewModel`; Android's `PlaybackService` drives it from a `MediaSession`, audio focus and `ACTION_AUDIO_BECOMING_NOISY`.

**Tech Stack:** .NET 10, Avalonia 12.1.3, CommunityToolkit.Mvvm, SoundFlow 1.4.1 (miniaudio), xUnit v3, .NET Android (API 26+).

**Spec:** `docs/superpowers/specs/2026-10-04-cue-shuffle-media-session-design.md`

## Global Constraints

- Branch: `cue-shuffle-media-session` (already created from up-to-date `main`).
- Nothing outside `src/Mkb.Mixer.Audio` may reference SoundFlow.
- Audio-thread code (taps, modifiers, ring buffer) must not lock or allocate per block, except one-off growth of scratch buffers.
- Cue mode default is **Off**; `CueMix` 0 = cue only, 1 = master only, blended with `Crossfader.Gains` (constant power).
- Ring buffer: capacity 200 ms, high-water 50 ms, whole frames only.
- Media controls: play/pause only. No next/previous.
- Audio focus: pause on `AUDIOFOCUS_LOSS`; pause-and-resume on transient loss **only** when the audio mode is ringtone / in-call / in-communication; ignore every other transient and may-duck loss; `SetWillPauseWhenDucked(true)`.
- Disconnects: `ACTION_AUDIO_BECOMING_NOISY` is the only disconnect signal. Never add a general Bluetooth-disconnect listener (a watch disconnecting must not stop the mix).
- Service held for 10 minutes after a media/focus/noisy pause; 10 s after an in-app pause (unchanged).
- Comments follow the repo's style: explain *why*, reference the original's bugs only where relevant.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test` from the repo root. Baseline: 93 passing.

## Review Focus

1. **Enabling a cue mode after decks were cued while Off** — audio tapped while Off must not accumulate and blast out when Split/Device turns on. Pinned in Task 3 (`CueBusTests.ActivatingDiscardsAnythingAddedWhileInactive`).
2. **Saved cue device missing at startup** (USB headphones unplugged) — must fall back to Off with a status message, never silently pick another device (which might be the speakers). Pinned in Task 5 (`RestoringAMissingCueDeviceFallsBackToOff`).
3. **The same `Track` object queued twice with shuffle + repeat** — must still return a track, never throw. Pinned in Task 1 (`ShuffleCopesWithTheSameTrackQueuedTwice`).
4. **Lock-screen Play after the user already resumed a deck by hand** — must not also start the other deck. Pinned in Task 2 (`ResumeDoesNothingExtraWhenTheUserAlreadyRestartedADeck`).
5. **Loading a new track onto a cued deck** — the new player must still feed the cue. Pinned in Task 4 (`CueSurvivesLoadingANewTrack`).

---

### Task 1: Per-deck shuffle and repeat

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`
- Modify: `src/Mkb.Mixer.Library/Settings.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (constructor, `SaveState()`)
- Test: `tests/Mkb.Mixer.Tests/ShuffleRepeatTests.cs` (create)

**Interfaces:**
- Produces: `DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null)`; observable `bool Shuffle`, `bool Repeat`; `AppSettings.DeckAShuffle/DeckARepeat/DeckBShuffle/DeckBRepeat` (bool).

- [ ] **Step 1: Write the failing tests**

Create `tests/Mkb.Mixer.Tests/ShuffleRepeatTests.cs`:

```csharp
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class ShuffleRepeatTests
{
    /// <summary>Always picks the highest index it is offered, so shuffle is deterministic.</summary>
    private sealed class LastRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
    }

    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static DeckViewModel Deck(params Track[] queue)
    {
        var deck = new DeckViewModel(new FakeDeck(DeckId.A), new FakeAudioEngine(), new LastRandom());
        foreach (Track t in queue) deck.Playlist.Add(t);
        return deck;
    }

    [Fact]
    public void WithBothOffTheTopTrackIsTakenAndRemoved()
    {
        Track a = T("a"), b = T("b");
        var deck = Deck(a, b);

        Assert.Same(a, deck.DequeueNext());
        Assert.Equal(new[] { b }, deck.Playlist);
    }

    [Fact]
    public void AnEmptyQueueYieldsNothing() => Assert.Null(Deck().DequeueNext());

    [Fact]
    public void RepeatPutsThePlayedTrackBackOnTheEnd()
    {
        Track a = T("a"), b = T("b");
        var deck = Deck(a, b);
        deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Equal(new[] { b, a }, deck.Playlist);
    }

    [Fact]
    public void ShuffleTakesFromAnywhereInTheQueue()
    {
        Track a = T("a"), b = T("b"), c = T("c");
        var deck = Deck(a, b, c);
        deck.Shuffle = true;

        Assert.Same(c, deck.DequeueNext());
        Assert.Equal(new[] { a, b }, deck.Playlist);
    }

    [Fact]
    public void ShuffleWithRepeatNeverPlaysTheSameTrackTwiceRunning()
    {
        Track a = T("a"), b = T("b"), c = T("c");
        var deck = Deck(a, b, c);
        deck.Shuffle = deck.Repeat = true;

        Track first = deck.DequeueNext()!;   // c, which repeat puts back on the end
        Track second = deck.DequeueNext()!;  // LastRandom would pick c again if allowed

        Assert.Same(c, first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ShuffleWithRepeatAndOneTrackKeepsPlayingIt()
    {
        Track a = T("a");
        var deck = Deck(a);
        deck.Shuffle = deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Same(a, deck.DequeueNext());
    }

    [Fact]
    public void ShuffleCopesWithTheSameTrackQueuedTwice()
    {
        // Adding the selected library track twice queues the same object twice.
        Track a = T("a");
        var deck = Deck(a, a);
        deck.Shuffle = deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Same(a, deck.DequeueNext());
    }

    [Fact]
    public void ShuffleAndRepeatAreRememberedPerDeck()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));
        vm.DeckA.Shuffle = true;
        vm.DeckB.Repeat = true;
        vm.SaveState();

        var reloaded = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));

        Assert.True(reloaded.DeckA.Shuffle);
        Assert.False(reloaded.DeckA.Repeat);
        Assert.False(reloaded.DeckB.Shuffle);
        Assert.True(reloaded.DeckB.Repeat);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ShuffleRepeatTests`
Expected: build failure — `DeckViewModel` has no 3-argument constructor, no `Shuffle`/`Repeat`.

- [ ] **Step 3: Implement**

In `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`:

Add `using System.Linq;` to the usings. Replace the fields and constructor:

```csharp
    private readonly IDeck _deck;
    private readonly IAudioEngine _engine;
    private readonly Random _random;
    private CancellationTokenSource? _analysis;
    private Track? _lastDequeued;

    /// <param name="random">Injectable so tests can make shuffle deterministic.</param>
    public DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null)
    {
        _deck = deck;
        _engine = engine;
        _random = random ?? Random.Shared;
        // SoundFlow raises this from its audio callback thread, so it has to be
        // marshalled before it touches anything bound to the UI.
        _deck.TrackEnded += (_, _) => Dispatcher.UIThread.Post(PlayNextFromPlaylist);
    }
```

Add beside the other `[ObservableProperty]` fields:

```csharp
    /// <summary>Take a random queued track rather than the top one.</summary>
    [ObservableProperty] private bool _shuffle;

    /// <summary>Put each played track back on the end of the queue, so it never runs dry.</summary>
    [ObservableProperty] private bool _repeat;
```

Replace `DequeueNext()`:

```csharp
    /// <summary>
    /// Pops the next queued track, honouring shuffle and repeat. The auto-cue and
    /// "play the next track when this one ends" both come through here.
    /// </summary>
    public Track? DequeueNext()
    {
        if (Playlist.Count == 0) return null;

        int index = 0;
        if (Shuffle && Playlist.Count > 1)
        {
            // Never the track just played, which repeat has put back on the end.
            // Compared by reference: the same library track can be queued twice.
            int[] candidates = Enumerable.Range(0, Playlist.Count)
                .Where(i => !ReferenceEquals(Playlist[i], _lastDequeued))
                .ToArray();
            if (candidates.Length == 0)
                candidates = Enumerable.Range(0, Playlist.Count).ToArray();
            index = candidates[_random.Next(candidates.Length)];
        }

        Track next = Playlist[index];
        Playlist.RemoveAt(index);
        if (Repeat) Playlist.Add(next);
        _lastDequeued = next;
        return next;
    }
```

In `src/Mkb.Mixer.Library/Settings.cs`, add to `AppSettings`:

```csharp
    public bool DeckAShuffle { get; set; }
    public bool DeckARepeat { get; set; }
    public bool DeckBShuffle { get; set; }
    public bool DeckBRepeat { get; set; }
```

In `MainViewModel`'s constructor, directly after `DeckB.PlaybackRefused += ...;`:

```csharp
        DeckA.Shuffle = _settings.DeckAShuffle;
        DeckA.Repeat = _settings.DeckARepeat;
        DeckB.Shuffle = _settings.DeckBShuffle;
        DeckB.Repeat = _settings.DeckBRepeat;
```

In `SaveState()` (the parameterless one), before `_settingsStore.Save(_settings);`:

```csharp
        _settings.DeckAShuffle = DeckA.Shuffle;
        _settings.DeckARepeat = DeckA.Repeat;
        _settings.DeckBShuffle = DeckB.Shuffle;
        _settings.DeckBRepeat = DeckB.Repeat;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass (93 + 8).

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs src/Mkb.Mixer.Library/Settings.cs src/Mkb.Mixer.App/ViewModels/MainViewModel.cs tests/Mkb.Mixer.Tests/ShuffleRepeatTests.cs
git commit -m "Add per-deck shuffle and repeat

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Pause-all and resume for media controls

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (add `State`)
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (add `PauseAll`, `ResumePaused`)
- Test: `tests/Mkb.Mixer.Tests/PauseAllTests.cs` (create)

**Interfaces:**
- Consumes: `DeckViewModel.PlayCommand`, `PauseCommand` (existing).
- Produces: `DeckViewModel.State` (`PlaybackState`); `bool MainViewModel.PauseAll()` (true when it paused anything); `void MainViewModel.ResumePaused()`. Task 7 calls both.

- [ ] **Step 1: Write the failing tests**

Create `tests/Mkb.Mixer.Tests/PauseAllTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter PauseAllTests`
Expected: build failure — `PauseAll`/`ResumePaused` not defined.

- [ ] **Step 3: Implement**

In `DeckViewModel`, next to `Id`:

```csharp
    /// <summary>The deck's live transport state, not the 100 ms-stale <see cref="IsPlaying"/>.</summary>
    public PlaybackState State => _deck.State;
```

In `MainViewModel`, add a field beside the others:

```csharp
    private readonly System.Collections.Generic.List<DeckViewModel> _pausedByPauseAll = [];
```

and these methods after `Tick`:

```csharp
    /// <summary>
    /// Pauses every playing deck and remembers which, for the platform's media
    /// controls and audio focus. A call with nothing playing leaves the memory of
    /// the previous pause intact, so a phone call arriving after a lock-screen pause
    /// cannot make the later resume forget what to restart.
    /// </summary>
    /// <returns>True when something was paused.</returns>
    public bool PauseAll()
    {
        DeckViewModel[] playing = new[] { DeckA, DeckB }
            .Where(d => d.State == PlaybackState.Playing)
            .ToArray();
        if (playing.Length == 0) return false;

        _pausedByPauseAll.Clear();
        foreach (DeckViewModel deck in playing)
        {
            deck.PauseCommand.Execute(null);
            _pausedByPauseAll.Add(deck);
        }
        return true;
    }

    /// <summary>
    /// Resumes what <see cref="PauseAll"/> paused. With nothing remembered and
    /// nothing playing, it presses play on the deck in front on the crossfader.
    /// </summary>
    public void ResumePaused()
    {
        DeckViewModel[] toResume = _pausedByPauseAll
            .Where(d => d.State == PlaybackState.Paused)
            .ToArray();
        _pausedByPauseAll.Clear();

        if (toResume.Length > 0)
        {
            foreach (DeckViewModel deck in toResume)
                deck.PlayCommand.Execute(null);
            return;
        }

        if (DeckA.State == PlaybackState.Playing || DeckB.State == PlaybackState.Playing)
            return;

        (CrossfaderPosition <= Crossfader.Centre ? DeckA : DeckB).PlayCommand.Execute(null);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs src/Mkb.Mixer.App/ViewModels/MainViewModel.cs tests/Mkb.Mixer.Tests/PauseAllTests.cs
git commit -m "Pause and resume the whole mix for platform media controls

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Cue maths — mode, mixing, bus and ring buffer

Pure code, no SoundFlow.

**Files:**
- Create: `src/Mkb.Mixer.Audio/CueMode.cs`
- Create: `src/Mkb.Mixer.Audio/CueMixing.cs`
- Create: `src/Mkb.Mixer.Audio/CueBus.cs`
- Create: `src/Mkb.Mixer.Audio/CueRingBuffer.cs`
- Test: `tests/Mkb.Mixer.Tests/CueTests.cs` (create)

**Interfaces:**
- Produces:
  - `enum CueMode { Off, Split, Device }`
  - `static void CueMixing.Blend(ReadOnlySpan<float> cue, ReadOnlySpan<float> master, float mix, Span<float> destination)`
  - `static void CueMixing.Split(Span<float> master, ReadOnlySpan<float> headphones)` (interleaved stereo, in place)
  - `CueBus`: `bool Active { get; set; }`, `void Add(ReadOnlySpan<float> block)`, `void Drain(Span<float> destination)`
  - `CueRingBuffer(int capacity, int highWater, int channels)`: `int Count`, `int Write(ReadOnlySpan<float>)`, `void Read(Span<float>)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Mkb.Mixer.Tests/CueTests.cs`:

```csharp
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CueMixingTests
{
    [Fact]
    public void MixAtZeroIsCueOnly()
    {
        var dest = new float[2];
        CueMixing.Blend([0.4f, -0.2f], [0.9f, 0.9f], 0f, dest);
        Assert.Equal(0.4f, dest[0], 5);
        Assert.Equal(-0.2f, dest[1], 5);
    }

    [Fact]
    public void MixAtOneIsMasterOnly()
    {
        var dest = new float[2];
        CueMixing.Blend([0.4f, -0.2f], [0.9f, 0.1f], 1f, dest);
        Assert.Equal(0.9f, dest[0], 5);
        Assert.Equal(0.1f, dest[1], 5);
    }

    [Fact]
    public void TheBlendIsConstantPower()
    {
        var dest = new float[1];
        CueMixing.Blend([1f], [1f], 0.5f, dest);
        Assert.Equal(2 * MathF.Sqrt(0.5f), dest[0], 4);
    }

    [Fact]
    public void SplitPutsMonoMasterLeftAndMonoHeadphonesRight()
    {
        float[] master = [1f, 0.5f, -1f, 0f];
        float[] phones = [0.2f, 0.4f, 0f, 1f];

        CueMixing.Split(master, phones);

        Assert.Equal(new[] { 0.75f, 0.3f, -0.5f, 0.5f }, master);
    }
}

public class CueBusTests
{
    [Fact]
    public void CuedBlocksAreSummed()
    {
        var bus = new CueBus { Active = true };
        bus.Add([0.1f, 0.2f]);
        bus.Add([0.3f, 0.4f]);

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(0.4f, dest[0], 5);
        Assert.Equal(0.6f, dest[1], 5);
    }

    [Fact]
    public void DrainingClearsForTheNextBlock()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        bus.Drain(new float[2]);

        var dest = new float[] { 9f, 9f };
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }

    [Fact]
    public void ADestinationLongerThanTheBlockIsPaddedWithSilence()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);

        var dest = new float[] { 9f, 9f, 9f, 9f };
        bus.Drain(dest);

        Assert.Equal(new[] { 1f, 1f, 0f, 0f }, dest);
    }

    [Fact]
    public void AnInactiveBusIgnoresBlocks()
    {
        var bus = new CueBus();
        bus.Add([1f, 1f]);

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }

    [Fact]
    public void ActivatingDiscardsAnythingAddedWhileInactive()
    {
        // Decks can be cued while the mode is Off. Turning a mode on must not
        // release a burst of audio that piled up in the meantime.
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        bus.Active = false;
        bus.Add([1f, 1f]);
        bus.Active = true;

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }
}

public class CueRingBufferTests
{
    private static float[] Range(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Fact]
    public void ReadsBackWhatWasWritten()
    {
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        ring.Write(Range(0, 4));

        var dest = new float[4];
        ring.Read(dest);

        Assert.Equal(Range(0, 4), dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void PadsWithSilenceWhenItRunsShort()
    {
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        ring.Write([1f, 2f]);

        var dest = new float[] { 9f, 9f, 9f, 9f };
        ring.Read(dest);

        Assert.Equal(new[] { 1f, 2f, 0f, 0f }, dest);
    }

    [Fact]
    public void DropsWhatDoesNotFitWhenFull()
    {
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        Assert.Equal(8, ring.Write(Range(0, 10)));
        Assert.Equal(8, ring.Count);
    }

    [Fact]
    public void SkipsAheadWhenTheReaderFallsTooFarBehind()
    {
        var ring = new CueRingBuffer(capacity: 64, highWater: 8, channels: 2);
        ring.Write(Range(0, 40));

        var dest = new float[4];
        ring.Read(dest);

        Assert.Equal(Range(36, 4), dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void WrapsAroundInOrder()
    {
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        ring.Write(Range(0, 6));
        ring.Read(new float[6]);
        ring.Write(Range(6, 6));

        var dest = new float[6];
        ring.Read(dest);

        Assert.Equal(Range(6, 6), dest);
    }

    [Fact]
    public void KeepsFramesWhole()
    {
        // A dropped half-frame would swap left and right for the rest of the set.
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        ring.Write(Range(0, 6));
        Assert.Equal(2, ring.Write(Range(6, 3)));
    }

    [Fact]
    public void RejectsACapacityThatIsNotWholeFrames() =>
        Assert.Throws<ArgumentException>(() => new CueRingBuffer(capacity: 7, highWater: 4, channels: 2));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "CueMixingTests|CueBusTests|CueRingBufferTests"`
Expected: build failure — types not defined.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Audio/CueMode.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>How the headphone cue reaches the headphones.</summary>
public enum CueMode
{
    /// <summary>No cue: the master output is normal stereo.</summary>
    Off,

    /// <summary>
    /// One stereo output shared through a splitter cable: the room mix folded to
    /// mono on the left, the headphone feed folded to mono on the right. Works on
    /// any device, phones included.
    /// </summary>
    Split,

    /// <summary>The headphone feed in stereo on a second playback device.</summary>
    Device
}
```

`src/Mkb.Mixer.Audio/CueMixing.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>The arithmetic behind the headphone cue, kept free of SoundFlow so it can be tested.</summary>
public static class CueMixing
{
    /// <summary>
    /// Blends the cue with the master for the headphones. Uses the crossfader's
    /// constant-power curve so the level holds steady as the knob turns.
    /// </summary>
    /// <param name="mix">0 is cue only, 1 is master only.</param>
    public static void Blend(ReadOnlySpan<float> cue, ReadOnlySpan<float> master, float mix, Span<float> destination)
    {
        var (cueGain, masterGain) = Crossfader.Gains(mix);
        for (int i = 0; i < destination.Length; i++)
            destination[i] = cue[i] * cueGain + master[i] * masterGain;
    }

    /// <summary>
    /// Rewrites interleaved stereo <paramref name="master"/> in place: left becomes
    /// the master folded to mono, right becomes the headphone feed folded to mono.
    /// </summary>
    public static void Split(Span<float> master, ReadOnlySpan<float> headphones)
    {
        for (int i = 0; i + 1 < master.Length; i += 2)
        {
            float room = (master[i] + master[i + 1]) * 0.5f;
            float phones = (headphones[i] + headphones[i + 1]) * 0.5f;
            master[i] = room;
            master[i + 1] = phones;
        }
    }
}
```

`src/Mkb.Mixer.Audio/CueBus.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>
/// Sums the cued decks' audio for one audio callback. Every deck's tap and the
/// master mixer's cue modifier run on the same audio thread, in that order, inside
/// one callback, so this needs no locking: taps <see cref="Add"/>, then the master
/// <see cref="Drain"/>s.
/// </summary>
public sealed class CueBus
{
    private float[] _sum = new float[4096];
    private int _length;
    private volatile bool _active;

    /// <summary>
    /// Off while no cue mode is on, so taps on cued decks do not pile audio up with
    /// nothing draining it. Switching on starts from silence.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value && !_active)
            {
                Array.Clear(_sum);
                _length = 0;
            }
            _active = value;
        }
    }

    public void Add(ReadOnlySpan<float> block)
    {
        if (!_active) return;
        if (block.Length > _sum.Length)
            Array.Resize(ref _sum, block.Length);   // rare: the device asked for a bigger block
        for (int i = 0; i < block.Length; i++)
            _sum[i] += block[i];
        _length = Math.Max(_length, block.Length);
    }

    /// <summary>Copies out this callback's cue, padding with silence, and clears for the next.</summary>
    public void Drain(Span<float> destination)
    {
        int n = Math.Min(_length, destination.Length);
        _sum.AsSpan(0, n).CopyTo(destination);
        destination[n..].Clear();
        _sum.AsSpan(0, _length).Clear();
        _length = 0;
    }
}
```

`src/Mkb.Mixer.Audio/CueRingBuffer.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>
/// Carries the headphone feed from the master device's audio thread to the cue
/// device's. Single producer, single consumer, lock-free.
/// </summary>
/// <remarks>
/// Two sound cards never run at exactly the same rate, so the fill level drifts.
/// The writer drops what does not fit; the reader pads with silence when it runs
/// short, and jumps to the newest audio when it falls more than
/// <c>highWater</c> samples behind, which keeps the headphone delay bounded
/// instead of growing over a long set. Everything moves in whole frames so left
/// and right never swap.
/// </remarks>
public sealed class CueRingBuffer
{
    private readonly float[] _data;
    private readonly int _highWater;
    private readonly int _channels;
    private long _written;
    private long _read;

    public CueRingBuffer(int capacity, int highWater, int channels)
    {
        if (channels <= 0 || capacity <= 0 || capacity % channels != 0)
            throw new ArgumentException("capacity must be a positive whole number of frames", nameof(capacity));
        _data = new float[capacity];
        _highWater = highWater;
        _channels = channels;
    }

    /// <summary>Samples waiting to be read.</summary>
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Producer side.</summary>
    /// <returns>How many samples were accepted.</returns>
    public int Write(ReadOnlySpan<float> samples)
    {
        long w = _written;
        int free = _data.Length - (int)(w - Volatile.Read(ref _read));
        int n = Math.Min(free, samples.Length);
        n -= n % _channels;
        for (int i = 0; i < n; i++)
            _data[(int)((w + i) % _data.Length)] = samples[i];
        Volatile.Write(ref _written, w + n);
        return n;
    }

    /// <summary>Consumer side. Always fills <paramref name="destination"/>.</summary>
    public void Read(Span<float> destination)
    {
        long r = _read;
        long w = Volatile.Read(ref _written);
        if (w - r > destination.Length + _highWater)
            r = w - destination.Length;   // fell behind: jump to the newest audio

        int n = (int)Math.Min(w - r, destination.Length);
        n -= n % _channels;
        for (int i = 0; i < n; i++)
            destination[i] = _data[(int)((r + i) % _data.Length)];
        destination[n..].Clear();
        Volatile.Write(ref _read, r + n);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.Audio/CueMode.cs src/Mkb.Mixer.Audio/CueMixing.cs src/Mkb.Mixer.Audio/CueBus.cs src/Mkb.Mixer.Audio/CueRingBuffer.cs tests/Mkb.Mixer.Tests/CueTests.cs
git commit -m "Add the headphone cue's mixing, bus and ring buffer

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Cue in the engine — interfaces, SoundFlow adapters, wiring

**Files:**
- Modify: `src/Mkb.Mixer.Audio/IDeck.cs`, `src/Mkb.Mixer.Audio/IAudioEngine.cs`
- Create: `src/Mkb.Mixer.Audio/SoundFlowCue.cs`
- Modify: `src/Mkb.Mixer.Audio/SoundFlowDeck.cs`, `src/Mkb.Mixer.Audio/SoundFlowAudioEngine.cs`
- Modify: `src/Mkb.Mixer.Audio/Mkb.Mixer.Audio.csproj` (InternalsVisibleTo)
- Modify: `tests/Mkb.Mixer.Tests/FakeDeck.cs`, `tests/Mkb.Mixer.Tests/FakeAudioEngine.cs`
- Test: `tests/Mkb.Mixer.Tests/CueEngineTests.cs` (create)

**Interfaces:**
- Consumes: everything Task 3 produced.
- Produces:
  - `IDeck.IsCued { get; set; }`
  - `IAudioEngine`: `CueMode CueMode { get; }`, `string? CueDevice { get; }`, `float CueMix { get; set; }`, `IReadOnlyList<string> CueDeviceNames()`, `bool TrySetCue(CueMode mode, string? deviceName, out string? error)`
  - `FakeAudioEngine`: `List<string> CueDeviceList` (default `["Fake headphones", "Fake USB"]`), `string? CueFailure` (forces any non-Off mode to fail)
  - internal `CueTap(CueBus)`, `CueSplitModifier(CueBus, Func<float>)`, `CueFeedModifier(CueBus, CueRingBuffer, Func<float>)`, `CueSource(AudioEngine, AudioFormat, CueRingBuffer)`

SoundFlow facts this task relies on (verified from the 1.4.1 IL):
- `SoundComponent.Process` runs `GenerateAudio` → modifiers → volume/pan → mix into parent → analyzers. A modifier therefore sees the deck **before** the crossfader gain.
- The device fills its buffer by calling `MasterMixer.Process`, whose `GenerateAudio` processes every deck, after which the master mixer's own modifiers run — same callback, same thread.
- `SoundModifier` has a parameterless constructor; `ProcessSample(float, int)` is abstract; `Process(Span<float>, int)` and `Name` are virtual.
- `SoundComponent(AudioEngine, AudioFormat)`; `GenerateAudio(Span<float>, int)` is protected abstract.
- The app mutes with `Volume = 0`, not SoundFlow's `Mute` (which skips processing and would silence the cue too). Keep it that way.

- [ ] **Step 1: Write the failing tests**

Create `tests/Mkb.Mixer.Tests/CueEngineTests.cs`:

```csharp
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CueAdapterTests
{
    [Fact]
    public void ACuedTapCopiesTheDeckIntoTheBusAndLeavesItUnchanged()
    {
        var bus = new CueBus { Active = true };
        var tap = new CueTap(bus) { IsCued = true };
        float[] block = [0.5f, -0.5f];

        tap.Process(block, 2);

        Assert.Equal(new[] { 0.5f, -0.5f }, block);
        var cue = new float[2];
        bus.Drain(cue);
        Assert.Equal(new[] { 0.5f, -0.5f }, cue);
    }

    [Fact]
    public void AnUncuedTapContributesNothing()
    {
        var bus = new CueBus { Active = true };
        new CueTap(bus).Process([0.5f, 0.5f], 2);

        var cue = new float[2];
        bus.Drain(cue);
        Assert.Equal(new[] { 0f, 0f }, cue);
    }

    [Fact]
    public void SplitSendsTheRoomLeftAndTheCueRight()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        var split = new CueSplitModifier(bus, () => 0f);   // headphones: cue only
        float[] master = [0.5f, 0.3f];

        split.Process(master, 2);

        Assert.Equal(0.4f, master[0], 5);
        Assert.Equal(1f, master[1], 5);
    }

    [Fact]
    public void SplitLeavesANonStereoOutputAlone()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f]);
        float[] master = [0.5f];

        new CueSplitModifier(bus, () => 0f).Process(master, 1);

        Assert.Equal(new[] { 0.5f }, master);
    }

    [Fact]
    public void TheFeedSendsTheBlendToTheRingWithoutTouchingTheRoom()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        float[] master = [0.2f, 0.2f];

        new CueFeedModifier(bus, ring, () => 0f).Process(master, 2);

        Assert.Equal(new[] { 0.2f, 0.2f }, master);
        var phones = new float[2];
        ring.Read(phones);
        Assert.Equal(new[] { 1f, 1f }, phones);
    }

    [Fact]
    public void CueSurvivesLoadingANewTrack()
    {
        // Each Load builds a new SoundFlow player; the deck's tap must move with it.
        var deck = new SoundFlowDeck(DeckId.A, engine: null, output: null, new CueBus());
        deck.IsCued = true;

        deck.Load(new Track("/m/next.mp3", "Next"));

        Assert.True(deck.IsCued);
    }
}

public class CueEngineTests
{
    [Fact]
    public void WithNoAudioOutputOnlyOffIsAccepted()
    {
        using var engine = new SoundFlowAudioEngine([]);

        Assert.False(engine.TrySetCue(CueMode.Split, null, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.True(engine.TrySetCue(CueMode.Off, null, out _));
    }

    [Fact]
    public void CueMixIsClamped()
    {
        using var engine = new SoundFlowAudioEngine([]);
        engine.CueMix = 3f;
        Assert.Equal(1f, engine.CueMix);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "CueAdapterTests|CueEngineTests"`
Expected: build failure — `CueTap` etc. not defined.

- [ ] **Step 3: Extend the interfaces and fakes**

`IDeck.cs` — after `IsMuted`:

```csharp
    /// <summary>
    /// Sends this deck to the headphones, taken before the crossfader and mute so
    /// a deck can be pre-listened while the room cannot hear it.
    /// </summary>
    bool IsCued { get; set; }
```

`IAudioEngine.cs` — before `ApplyCrossfader`:

```csharp
    /// <summary>How the headphone cue is currently routed.</summary>
    CueMode CueMode { get; }

    /// <summary>The cue device's name in <see cref="Audio.CueMode.Device"/> mode, otherwise null.</summary>
    string? CueDevice { get; }

    /// <summary>What the headphones hear: 0 is cued decks only, 1 is the room mix only.</summary>
    float CueMix { get; set; }

    /// <summary>Real playback devices the cue can go to. Dummy sinks are left out.</summary>
    IReadOnlyList<string> CueDeviceNames();

    /// <summary>
    /// Switches cue routing. On failure the cue is left Off and
    /// <paramref name="error"/> says why.
    /// </summary>
    bool TrySetCue(CueMode mode, string? deviceName, out string? error);
```

`FakeDeck.cs` — add `public bool IsCued { get; set; }` after `IsMuted`.

`FakeAudioEngine.cs` — add after `LastCrossfader`:

```csharp
    public CueMode CueMode { get; private set; }
    public string? CueDevice { get; private set; }
    public float CueMix { get; set; }
    public List<string> CueDeviceList { get; } = ["Fake headphones", "Fake USB"];

    /// <summary>When set, every mode except Off fails with this message.</summary>
    public string? CueFailure { get; set; }

    public IReadOnlyList<string> CueDeviceNames() => CueDeviceList;

    public bool TrySetCue(CueMode mode, string? deviceName, out string? error)
    {
        error = mode == CueMode.Off ? null
            : CueFailure
              ?? (mode == CueMode.Device && !CueDeviceList.Contains(deviceName ?? "")
                  ? $"cue device \"{deviceName}\" not found"
                  : null);
        bool ok = error is null;
        CueMode = ok ? mode : CueMode.Off;
        CueDevice = ok && mode == CueMode.Device ? deviceName : null;
        return ok;
    }
```

`Mkb.Mixer.Audio.csproj` — add:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Mkb.Mixer.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Write the SoundFlow adapters**

Create `src/Mkb.Mixer.Audio/SoundFlowCue.cs`:

```csharp
using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace Mkb.Mixer.Audio;

// The headphone cue's SoundFlow plumbing. Kept thin: the arithmetic lives in
// CueMixing, CueBus and CueRingBuffer, which are tested without a sound card.

/// <summary>
/// Sits on a deck's player. SoundFlow runs modifiers before the component's
/// volume, so this sees the deck before the crossfader and mute are applied.
/// </summary>
internal sealed class CueTap(CueBus bus) : SoundModifier
{
    private volatile bool _cued;

    public override string Name { get; set; } = "Cue tap";

    public bool IsCued
    {
        get => _cued;
        set => _cued = value;
    }

    public override void Process(Span<float> buffer, int channels)
    {
        if (_cued) bus.Add(buffer);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>
/// Split mode, on the master mixer: left becomes the room mix in mono, right the
/// headphone feed in mono. Runs after every deck in the same callback, so the bus
/// holds exactly this block's cue.
/// </summary>
internal sealed class CueSplitModifier(CueBus bus, Func<float> mix) : SoundModifier
{
    private float[] _cue = [];
    private float[] _phones = [];

    public override string Name { get; set; } = "Cue split";

    public override void Process(Span<float> buffer, int channels)
    {
        Span<float> cue = CueScratch.Get(ref _cue, buffer.Length);
        bus.Drain(cue);
        if (channels != 2) return;   // split needs a left and a right; leave anything else alone

        Span<float> phones = CueScratch.Get(ref _phones, buffer.Length);
        CueMixing.Blend(cue, buffer, mix(), phones);
        CueMixing.Split(buffer, phones);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>
/// Device mode, on the master mixer: hands the headphone feed to the cue device
/// through the ring buffer and leaves the room mix untouched.
/// </summary>
internal sealed class CueFeedModifier(CueBus bus, CueRingBuffer ring, Func<float> mix) : SoundModifier
{
    private float[] _cue = [];
    private float[] _phones = [];

    public override string Name { get; set; } = "Cue feed";

    public override void Process(Span<float> buffer, int channels)
    {
        Span<float> cue = CueScratch.Get(ref _cue, buffer.Length);
        bus.Drain(cue);
        Span<float> phones = CueScratch.Get(ref _phones, buffer.Length);
        CueMixing.Blend(cue, buffer, mix(), phones);
        ring.Write(phones);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>Device mode, on the cue device's mixer: plays what the feed wrote.</summary>
internal sealed class CueSource(AudioEngine engine, AudioFormat format, CueRingBuffer ring)
    : SoundComponent(engine, format)
{
    public override string Name { get; set; } = "Cue";

    protected override void GenerateAudio(Span<float> buffer, int channels) => ring.Read(buffer);
}

internal static class CueScratch
{
    /// <summary>A reusable buffer, grown only when a device asks for a bigger block.</summary>
    public static Span<float> Get(ref float[] array, int length)
    {
        if (array.Length < length) array = new float[length];
        return array.AsSpan(0, length);
    }
}
```

If the compiler reports a different access modifier for `GenerateAudio` (e.g. `protected internal`), match it exactly — SoundFlow's declaration wins.

- [ ] **Step 5: Wire the tap into the deck**

`SoundFlowDeck.cs`:

Constructor and field:

```csharp
    private readonly CueTap _tap;

    public SoundFlowDeck(DeckId id, MiniAudioEngine? engine, SoundFlow.Components.Mixer? output, CueBus cueBus)
    {
        Id = id;
        _engine = engine;
        _output = output;
        // One tap for the deck's lifetime, moved onto each new player in Load, so
        // the cue stays on across track changes.
        _tap = new CueTap(cueBus);
    }
```

Property, after `IsMuted`:

```csharp
    public bool IsCued
    {
        get => _tap.IsCued;
        set => _tap.IsCued = value;
    }
```

In `Load`, directly after `_player.PlaybackEnded += OnPlaybackEnded;`:

```csharp
            _player.AddModifier(_tap);
```

In `TearDownPlayer`, before `_player.Dispose();`:

```csharp
            _player.RemoveModifier(_tap);
```

- [ ] **Step 6: Wire the cue into the engine**

`SoundFlowAudioEngine.cs`:

Add `using SoundFlow.Abstracts;` to the usings. Add fields after `_diagnostics`:

```csharp
    private readonly CueBus _cueBus = new();
    private AudioFormat _outputFormat = Format;
    private SoundModifier? _cueModifier;
    private AudioPlaybackDevice? _cueDevice;
    private volatile float _cueMix;
```

In `TryTargets`, inside the `try` after `opened = device;`:

```csharp
                    _outputFormat = format;
```

Pass the bus to both decks:

```csharp
        _a = new SoundFlowDeck(DeckId.A, _engine, _device?.MasterMixer, _cueBus);
        _b = new SoundFlowDeck(DeckId.B, _engine, _device?.MasterMixer, _cueBus);
```

Add the cue members after `OnlyDummyDevices`:

```csharp
    public CueMode CueMode { get; private set; }
    public string? CueDevice { get; private set; }

    public float CueMix
    {
        get => _cueMix;
        set => _cueMix = Math.Clamp(value, 0f, 1f);
    }

    public IReadOnlyList<string> CueDeviceNames()
    {
        if (_engine is null) return [];
        try
        {
            _engine.UpdateAudioDevicesInfo();
            return _engine.PlaybackDevices
                .Where(d => !IsDummyDevice(d.Name))
                .Select(d => d.Name)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public bool TrySetCue(CueMode mode, string? deviceName, out string? error)
    {
        error = null;
        TearDownCue();
        if (mode == CueMode.Off) return true;

        if (!IsOutputAvailable || _engine is null || _device is null)
        {
            error = "there is no audio output";
            return false;
        }

        if (mode == CueMode.Split)
        {
            _cueModifier = new CueSplitModifier(_cueBus, () => _cueMix);
            _device.MasterMixer.AddModifier(_cueModifier);
            _cueBus.Active = true;
            CueMode = CueMode.Split;
            return true;
        }

        DeviceInfo[] matches;
        try
        {
            _engine.UpdateAudioDevicesInfo();
            matches = _engine.PlaybackDevices.Where(d => d.Name == deviceName).ToArray();
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        if (matches.Length == 0)
        {
            error = $"cue device \"{deviceName}\" not found";
            return false;
        }

        try
        {
            // The cue device runs at the master's format, so the ring carries
            // samples at the rate the cue device consumes them.
            var ring = new CueRingBuffer(
                capacity: _outputFormat.SampleRate / 5 * _outputFormat.Channels,    // 200 ms
                highWater: _outputFormat.SampleRate / 20 * _outputFormat.Channels,  // 50 ms
                channels: _outputFormat.Channels);
            AudioPlaybackDevice cue = _engine.InitializePlaybackDevice(matches[0], _outputFormat);
            _cueDevice = cue;
            cue.MasterMixer.AddComponent(new CueSource(_engine, _outputFormat, ring));
            cue.Start();

            _cueModifier = new CueFeedModifier(_cueBus, ring, () => _cueMix);
            _device.MasterMixer.AddModifier(_cueModifier);
            _cueBus.Active = true;
            CueMode = CueMode.Device;
            CueDevice = matches[0].Name;
            return true;
        }
        catch (Exception e)
        {
            TearDownCue();
            error = e.Message;
            return false;
        }
    }

    private void TearDownCue()
    {
        _cueBus.Active = false;
        if (_cueModifier is not null)
        {
            _device?.MasterMixer.RemoveModifier(_cueModifier);
            _cueModifier = null;
        }
        if (_cueDevice is not null)
        {
            try { _cueDevice.Dispose(); } catch { /* already gone, e.g. unplugged */ }
            _cueDevice = null;
        }
        CueMode = CueMode.Off;
        CueDevice = null;
    }
```

In `Dispose()`, after `_disposed = true;`:

```csharp
        TearDownCue();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. The App project still compiles because nothing in it implements `IDeck`/`IAudioEngine`.

- [ ] **Step 8: Commit**

```bash
git add src/Mkb.Mixer.Audio tests/Mkb.Mixer.Tests/FakeDeck.cs tests/Mkb.Mixer.Tests/FakeAudioEngine.cs tests/Mkb.Mixer.Tests/CueEngineTests.cs
git commit -m "Route cued decks to the headphones in Split or Device mode

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Cue in the view models and settings

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs`
- Modify: `src/Mkb.Mixer.Library/Settings.cs`
- Test: `tests/Mkb.Mixer.Tests/CueViewModelTests.cs` (create)

**Interfaces:**
- Consumes: Task 4's `IAudioEngine` cue members and `FakeAudioEngine.CueDeviceList/CueFailure`.
- Produces (bound by Task 6's XAML):
  - `DeckViewModel`: observable `bool IsCued`, `bool IsCueAvailable`
  - `MainViewModel`: `IReadOnlyList<CueMode> CueModes`, `ObservableCollection<string> CueDevices`, observable `CueMode CueMode`, `string? CueDevice`, `float CueMix`, computed `bool IsCueDeviceMode`
  - `AppSettings.CueMode` (`CueMode`), `CueDevice` (`string?`), `CueMix` (`float`, default 0)

- [ ] **Step 1: Write the failing tests**

Create `tests/Mkb.Mixer.Tests/CueViewModelTests.cs`:

```csharp
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class CueViewModelTests
{
    private static string NewSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json");

    private static MainViewModel Vm(FakeAudioEngine engine, string? path = null) =>
        new(engine, new SettingsStore(path ?? NewSettingsPath()));

    [Fact]
    public void CueIsOffAndDeckCueButtonsUnavailableByDefault()
    {
        var vm = Vm(new FakeAudioEngine());

        Assert.Equal(CueMode.Off, vm.CueMode);
        Assert.False(vm.DeckA.IsCueAvailable);
        Assert.False(vm.DeckB.IsCueAvailable);
    }

    [Fact]
    public void ChoosingSplitRoutesTheEngineAndEnablesDeckCue()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMode = CueMode.Split;

        Assert.Equal(CueMode.Split, engine.CueMode);
        Assert.True(vm.DeckA.IsCueAvailable);
        Assert.True(vm.DeckB.IsCueAvailable);
    }

    [Fact]
    public void ChoosingDeviceListsDevicesAndPicksTheFirst()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMode = CueMode.Device;

        Assert.Equal(new[] { "Fake headphones", "Fake USB" }, vm.CueDevices);
        Assert.True(vm.IsCueDeviceMode);
        Assert.Equal("Fake headphones", vm.CueDevice);
        Assert.Equal("Fake headphones", engine.CueDevice);
    }

    [Fact]
    public void PickingAnotherDeviceReroutes()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);
        vm.CueMode = CueMode.Device;

        vm.CueDevice = "Fake USB";

        Assert.Equal("Fake USB", engine.CueDevice);
    }

    [Fact]
    public void AFailedModeFallsBackToOffAndSaysWhy()
    {
        var engine = new FakeAudioEngine { CueFailure = "device busy" };
        var vm = Vm(engine);

        vm.CueMode = CueMode.Split;

        Assert.Equal(CueMode.Off, vm.CueMode);
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.False(vm.DeckA.IsCueAvailable);
        Assert.Contains("device busy", vm.StatusMessage);
    }

    [Fact]
    public void CueingADeckReachesTheEngineDeck()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.DeckB.IsCued = true;

        Assert.True(engine.B.IsCued);
        Assert.False(engine.A.IsCued);
    }

    [Fact]
    public void TheCueMixKnobReachesTheEngine()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMix = 0.7f;

        Assert.Equal(0.7f, engine.CueMix);
    }

    [Fact]
    public void CueSettingsAreRememberedAndReapplied()
    {
        string path = NewSettingsPath();
        var vm = Vm(new FakeAudioEngine(), path);
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.CueMix = 0.25f;
        vm.SaveState();

        var engine = new FakeAudioEngine();
        var reloaded = Vm(engine, path);

        Assert.Equal(CueMode.Device, reloaded.CueMode);
        Assert.Equal("Fake USB", engine.CueDevice);
        Assert.Equal(0.25f, engine.CueMix);
    }

    [Fact]
    public void RestoringAMissingCueDeviceFallsBackToOff()
    {
        // The USB headphones were unplugged between runs. Picking another device
        // instead could put the headphone feed on the room speakers.
        string path = NewSettingsPath();
        var vm = Vm(new FakeAudioEngine(), path);
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.SaveState();

        var engine = new FakeAudioEngine();
        engine.CueDeviceList.Remove("Fake USB");
        var reloaded = Vm(engine, path);

        Assert.Equal(CueMode.Off, reloaded.CueMode);
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.Contains("Fake USB", reloaded.StatusMessage);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter CueViewModelTests`
Expected: build failure — `CueMode`/`IsCueAvailable` etc. not on the view models.

- [ ] **Step 3: Implement**

`Settings.cs` — add `using Mkb.Mixer.Audio;` at the top and to `AppSettings`:

```csharp
    public CueMode CueMode { get; set; } = CueMode.Off;
    public string? CueDevice { get; set; }
    public float CueMix { get; set; }
```

`DeckViewModel` — beside the other observable fields:

```csharp
    /// <summary>Whether this deck is sent to the headphones.</summary>
    [ObservableProperty] private bool _isCued;

    /// <summary>False while the headphone mode is Off, which greys the deck's CUE button.</summary>
    [ObservableProperty] private bool _isCueAvailable;
```

and beside `OnTempoChanged`:

```csharp
    partial void OnIsCuedChanged(bool value) => _deck.IsCued = value;
```

`MainViewModel` — properties beside the other observables:

```csharp
    public System.Collections.Generic.IReadOnlyList<CueMode> CueModes { get; } =
        [CueMode.Off, CueMode.Split, CueMode.Device];

    /// <summary>Outputs the headphones can be on, in Device mode.</summary>
    public ObservableCollection<string> CueDevices { get; } = [];

    [ObservableProperty] private CueMode _cueMode;
    [ObservableProperty] private string? _cueDevice;

    /// <summary>What the headphones hear: 0 is cued decks only, 1 is the room mix only.</summary>
    [ObservableProperty] private float _cueMix;

    public bool IsCueDeviceMode => CueMode == CueMode.Device;

    private bool _applyingCue;
```

Handlers (beside the other `partial void On...Changed`):

```csharp
    partial void OnCueModeChanged(CueMode value)
    {
        OnPropertyChanged(nameof(IsCueDeviceMode));
        if (value == CueMode.Device) RefreshCueDevices();
        ApplyCue();
    }

    partial void OnCueDeviceChanged(string? value)
    {
        if (CueMode == CueMode.Device) ApplyCue();
    }

    partial void OnCueMixChanged(float value) => _engine.CueMix = value;

    private void RefreshCueDevices()
    {
        _applyingCue = true;   // filling the list must not reroute once per item
        try
        {
            CueDevices.Clear();
            foreach (string name in _engine.CueDeviceNames())
                CueDevices.Add(name);
            // Only fill a blank choice. A remembered device that has gone missing is
            // kept so applying it fails loudly, rather than silently choosing another
            // output that might be the room speakers.
            CueDevice ??= CueDevices.FirstOrDefault();
        }
        finally { _applyingCue = false; }
    }

    private void ApplyCue()
    {
        if (_applyingCue) return;
        _applyingCue = true;
        try
        {
            if (!_engine.TrySetCue(CueMode, CueDevice, out string? error))
            {
                StatusMessage = $"Headphone cue is off: {error}";
                CueMode = CueMode.Off;
                _engine.TrySetCue(CueMode.Off, null, out _);
            }
            DeckA.IsCueAvailable = DeckB.IsCueAvailable = CueMode != CueMode.Off;
        }
        finally { _applyingCue = false; }
    }
```

Constructor — at the very end (after the `foreach (string line in engine.Diagnostics)` loop), so a cue failure message is not overwritten by the audio status:

```csharp
        // Restore the headphone cue last, so if it cannot be re-applied (USB
        // headphones unplugged since last time) its message is the one shown.
        _cueMix = _settings.CueMix;
        engine.CueMix = _cueMix;
        _cueDevice = _settings.CueDevice;
        CueMode = _settings.CueMode;
```

`SaveState()` — before `_settingsStore.Save(_settings);`:

```csharp
        _settings.CueMode = CueMode;
        _settings.CueDevice = CueDevice;
        _settings.CueMix = CueMix;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App/ViewModels src/Mkb.Mixer.Library/Settings.cs tests/Mkb.Mixer.Tests/CueViewModelTests.cs
git commit -m "Choose and remember the headphone cue mode, device and mix

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Deck toggles and the Headphones row

**Files:**
- Modify: `src/Mkb.Mixer.App/Views/DeckView.axaml`
- Modify: `src/Mkb.Mixer.App/Views/MainView.axaml` (mixer panel)
- Modify: `src/Mkb.Mixer.App/Views/PhoneView.axaml` (Mix tab crossfader panel)
- Modify: `src/Mkb.Mixer.App/Styles.axaml`
- Test: `tests/Mkb.Mixer.Tests/UiSmokeTests.cs`

**Interfaces:**
- Consumes: Task 1 `Shuffle`, `Repeat`; Task 5 `IsCued`, `IsCueAvailable`, `CueModes`, `CueMode`, `CueDevices`, `CueDevice`, `CueMix`, `IsCueDeviceMode`.

- [ ] **Step 1: Write the failing test**

In `UiSmokeTests.cs`, add `using Avalonia.Controls.Primitives;` and this test:

```csharp
    [Fact]
    public void DecksShowShuffleRepeatAndCueToggles() => AvaloniaTest.Run(() =>
    {
        var (window, vm, _) = Build();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        List<ToggleButton> toggles = window.GetLogicalDescendants().OfType<ToggleButton>().ToList();
        List<ToggleButton> cues = toggles.Where(t => t.Content as string == "CUE").ToList();
        Assert.Equal(2, cues.Count);
        Assert.Equal(2, toggles.Count(t => t.Content as string == "⇄"));
        Assert.Equal(2, toggles.Count(t => t.Content as string == "↻"));
        Assert.All(cues, c => Assert.False(c.IsEffectivelyEnabled));

        vm.CueMode = CueMode.Split;
        Dispatcher.UIThread.RunJobs();

        Assert.All(cues, c => Assert.True(c.IsEffectivelyEnabled));
    });
```

In `RendersPopulatedLayoutToPng`, before `window.Show();`, add so the snapshot shows the new controls in use:

```csharp
        vm.CueMode = CueMode.Device;
        vm.DeckB.IsCued = true;
        vm.DeckA.Shuffle = true;
```

In `RendersPhoneLayoutToPng`, before `var phone = new PhoneView ...`, add:

```csharp
        vm.CueMode = CueMode.Split;
        vm.DeckA.IsCued = true;
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter DecksShowShuffleRepeatAndCueToggles`
Expected: FAIL — `Assert.Equal(2, cues.Count)` finds 0.

- [ ] **Step 3: Implement**

`Styles.axaml` — after the `Button.tiny` style:

```xml
  <Style Selector="ToggleButton.decktoggle">
    <Setter Property="Padding" Value="8,2" />
    <Setter Property="Margin" Value="4,0,0,0" />
    <Setter Property="MinWidth" Value="32" />
    <Setter Property="FontSize" Value="12" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
  </Style>
```

`DeckView.axaml` — replace the header `Grid` (the one holding the label and mute button):

```xml
      <!-- Deck name, shuffle/repeat/cue toggles and the live/mute indicator -->
      <Grid Grid.Row="0" ColumnDefinitions="*,Auto,Auto,Auto,Auto">
        <TextBlock Classes="heading" Text="{Binding Label}" VerticalAlignment="Center" />
        <ToggleButton Grid.Column="1" Classes="decktoggle" Content="⇄"
                      IsChecked="{Binding Shuffle}"
                      ToolTip.Tip="Shuffle: take a random track from this deck's queue" />
        <ToggleButton Grid.Column="2" Classes="decktoggle" Content="↻"
                      IsChecked="{Binding Repeat}"
                      ToolTip.Tip="Repeat: played tracks go back on the end of this deck's queue" />
        <ToggleButton Grid.Column="3" Classes="decktoggle" Content="CUE"
                      IsChecked="{Binding IsCued}" IsEnabled="{Binding IsCueAvailable}"
                      ToolTip.Tip="Send this deck to the headphones. Pick a headphone mode under the crossfader first." />
        <Button Grid.Column="4" Command="{Binding ToggleMuteCommand}"
                Background="Transparent" BorderThickness="0" Padding="6,2"
                ToolTip.Tip="Toggle mute for this deck">
          <TextBlock Text="{Binding MuteLabel}" FontSize="12" FontWeight="Bold"
                     Foreground="{Binding IsMuted, Converter={StaticResource MuteColour}}" />
        </Button>
      </Grid>
```

`MainView.axaml` — in the mixer `Border`, change the inner `Grid` to `<Grid RowDefinitions="Auto,Auto" ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto">` and add, before its closing `</Grid>`:

```xml
        <!-- Headphone cue -->
        <StackPanel Grid.Row="1" Grid.ColumnSpan="7" Orientation="Horizontal"
                    Spacing="8" Margin="0,8,0,0">
          <TextBlock Classes="dim" Text="headphones" VerticalAlignment="Center" />
          <ComboBox ItemsSource="{Binding CueModes}" SelectedItem="{Binding CueMode}" MinWidth="110"
                    ToolTip.Tip="Off: normal stereo. Split: room mix on the left, headphones on the right, for a splitter cable. Device: headphones on a second output." />
          <ComboBox ItemsSource="{Binding CueDevices}" SelectedItem="{Binding CueDevice}" MinWidth="220"
                    IsVisible="{Binding IsCueDeviceMode}"
                    ToolTip.Tip="The output your headphones are plugged into" />
          <TextBlock Classes="dim" Text="cue" VerticalAlignment="Center" Margin="16,0,0,0" />
          <Slider Minimum="0" Maximum="1" Width="160" Value="{Binding CueMix}"
                  ToolTip.Tip="What the headphones hear: cued decks to the left, the room mix to the right" />
          <TextBlock Classes="dim" Text="master" VerticalAlignment="Center" />
        </StackPanel>
```

`PhoneView.axaml` — in the Mix tab's crossfader `Border`, change its `Grid` to `RowDefinitions="Auto,Auto,Auto"` and add after the auto-cue `StackPanel`:

```xml
                <!-- Headphone cue. Split is the mode phones can actually use. -->
                <WrapPanel Grid.Row="2" Grid.ColumnSpan="3" HorizontalAlignment="Center"
                           Margin="0,6,0,0">
                  <TextBlock Classes="dim" Text="headphones" VerticalAlignment="Center" Margin="0,0,8,0" />
                  <ComboBox ItemsSource="{Binding CueModes}" SelectedItem="{Binding CueMode}"
                            MinWidth="100" Margin="0,0,8,4" />
                  <ComboBox ItemsSource="{Binding CueDevices}" SelectedItem="{Binding CueDevice}"
                            MinWidth="160" Margin="0,0,8,4" IsVisible="{Binding IsCueDeviceMode}" />
                  <TextBlock Classes="dim" Text="cue" VerticalAlignment="Center" Margin="0,0,4,0" />
                  <Slider Minimum="0" Maximum="1" Width="120" Value="{Binding CueMix}" />
                  <TextBlock Classes="dim" Text="master" VerticalAlignment="Center" Margin="4,0,0,0" />
                </WrapPanel>
```

- [ ] **Step 4: Run the tests and look at the snapshots**

Run: `MKB_UI_SNAPSHOT=/tmp/claude-0/-root-Claude-MkbMixer/24e98113-ae3d-4673-85d4-3d07b703b812/scratchpad/mkb-mixer-ui.png dotnet test`
Expected: all pass. Open `mkb-mixer-ui.png` and `mkb-mixer-phone-0.png` in the scratchpad directory and check that the toggles sit on the deck header line without clipping the deck label, and the headphones row fits under the crossfader on both layouts.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App/Views src/Mkb.Mixer.App/Styles.axaml tests/Mkb.Mixer.Tests/UiSmokeTests.cs
git commit -m "Show shuffle, repeat and cue on each deck, and a headphones row by the crossfader

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Android media session, audio focus and becoming-noisy

There is no Android test host; the deliverable is a clean build plus the device checklist in Step 3.

**Files:**
- Modify: `src/Mkb.Mixer.App.Android/PlaybackService.cs` (full replacement below)

**Interfaces:**
- Consumes: `MainViewModel.PauseAll()`, `ResumePaused()`, `IsAnyDeckPlaying`, `PlayingSummary` (Task 2 and existing).

- [ ] **Step 1: Replace `PlaybackService.cs`**

```csharp
using System;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// Keeps the process alive while a deck is playing, and connects the mix to the
/// system's media controls. Without the service Android freezes or kills the app
/// shortly after it leaves the screen, which silences the mix and stops the
/// auto-cue clock that drives the next transition.
/// </summary>
/// <remarks>
/// The decks and the engine live in <see cref="MainViewModel"/>, not here: this
/// service tells the system that audio is playing, carries the notification a
/// foreground service must show, and owns the media session, audio focus and the
/// "becoming noisy" receiver, all of which pause or resume the whole mix through
/// <see cref="MainViewModel.PauseAll"/> and <see cref="MainViewModel.ResumePaused"/>.
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const string ChannelId = "playback";
    private const int NotificationId = 1;
    private const string ExtraText = "text";
    private const string ActionPlay = "com.mkbmain.mixer.PLAY";
    private const string ActionPause = "com.mkbmain.mixer.PAUSE";

    /// <summary>
    /// How long both decks must be silent before the service stops. Android 12+
    /// refuses to start a foreground service from the background, so stopping in
    /// the gap between one track ending and the next starting would leave the next
    /// one unprotected.
    /// </summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// After a pause from the lock screen, a headset button, a phone call or the
    /// output disconnecting, the service stays up this long so the play button keeps
    /// working: once it stops, Android 12+ will not let the app start it again from
    /// the background.
    /// </summary>
    private static readonly TimeSpan HeldStopDelay = TimeSpan.FromMinutes(10);

    private static MainViewModel? _watched;
    private static IDisposable? _pendingStop;
    private static bool _running;
    private static bool _held;
    private static bool _pausedForCall;
    private static string _text = string.Empty;
    private static PlaybackService? _instance;

    private PowerManager.WakeLock? _wakeLock;
    private MediaSession? _session;
    private AudioFocusRequestClass? _focusRequest;
    private readonly NoisyReceiver _noisy = new();

    /// <summary>Starts and stops the service as the view model's playback state changes.</summary>
    public static void Watch(MainViewModel viewModel)
    {
        // The activity can be recreated around the same view model; subscribe once.
        if (ReferenceEquals(_watched, viewModel)) return;
        _watched = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private static void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsAnyDeckPlaying) when vm.IsAnyDeckPlaying:
                _pendingStop?.Dispose();
                _pendingStop = null;
                _held = false;
                _text = vm.PlayingSummary;
                // A running service is updated in place; asking to start it again
                // from the background could be refused on Android 12+.
                if (_running && _instance is not null) _instance.OnResumed();
                else Start(vm);
                break;
            case nameof(MainViewModel.IsAnyDeckPlaying):
                _pendingStop ??= DispatcherTimer.RunOnce(Stop, _held ? HeldStopDelay : StopDelay);
                _instance?.Refresh();
                break;
            case nameof(MainViewModel.PlayingSummary) when _running && vm.IsAnyDeckPlaying:
                _text = vm.PlayingSummary;
                _instance?.Refresh();
                break;
        }
    }

    private static void Start(MainViewModel vm)
    {
        Context context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(PlaybackService)).PutExtra(ExtraText, vm.PlayingSummary);
        try
        {
            context.StartForegroundService(intent);
            _running = true;
        }
        catch (Java.Lang.IllegalStateException)   // ForegroundServiceStartNotAllowedException on 12+
        {
            // Playback began while the app was already in the background, which
            // only happens if the service stopped mid-mix. Nothing better to do
            // than say so; the audio itself carries on until the system steps in.
            vm.StatusMessage = "Background playback is not protected: open the app again to resume it";
        }
    }

    private static void Stop()
    {
        _pendingStop = null;
        _held = false;
        if (!_running) return;
        _running = false;
        Context context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(PlaybackService)));
    }

    /// <summary>A pause the user did not make in the app: hold the service so play still works.</summary>
    private static void PauseFromSystem()
    {
        if (_watched?.PauseAll() == true) _held = true;
    }

    private static void ResumeFromSystem()
    {
        _pausedForCall = false;
        _watched?.ResumePaused();
    }

    private static bool IsPlaying => _watched?.IsAnyDeckPlaying == true;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;

        _session = new MediaSession(this, "MkbMixer");
        _session.SetCallback(new SessionCallback());
        _session.Active = true;

        // Sent only when the output carrying our audio goes away (wired headphones,
        // a splitter, a Bluetooth speaker or headphones). A Bluetooth watch or car
        // kit disconnecting does not send it, which is exactly the behaviour wanted;
        // do not swap this for a general Bluetooth-disconnect listener.
        var noisy = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_noisy, noisy, ReceiverFlags.NotExported);
        else
            RegisterReceiver(_noisy, noisy);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.GetStringExtra(ExtraText) is { } text) _text = text;

        // Must reach StartForeground within a few seconds of StartForegroundService,
        // every time, or the system kills the app. That includes the notification's
        // own play/pause buttons, which start the service to deliver their action.
        Notification notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);
        _running = true;

        if (_wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            // The auto-cue runs on the UI thread's clock, not the audio callback, so
            // the CPU must stay up between audio buffers for transitions to happen.
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "MkbMixer:playback");
            _wakeLock?.Acquire();
        }

        switch (intent?.Action)
        {
            case ActionPause:
                Dispatcher.UIThread.Post(PauseFromSystem);
                break;
            case ActionPlay:
                Dispatcher.UIThread.Post(ResumeFromSystem);
                break;
            default:
                RequestFocus();
                break;
        }
        UpdateSession();

        // If the system kills the process the decks are gone with it; restarting
        // an empty service would only show a stale notification.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        _instance = null;
        UnregisterReceiver(_noisy);
        if (_focusRequest is not null && GetSystemService(AudioService) is AudioManager audio)
            audio.AbandonAudioFocusRequest(_focusRequest);
        _session?.Release();
        _session = null;
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        _wakeLock = null;
        base.OnDestroy();
    }

    /// <summary>Playback restarted while the service was already up.</summary>
    private void OnResumed()
    {
        RequestFocus();
        Refresh();
    }

    /// <summary>Brings the notification and the media session up to date.</summary>
    private void Refresh()
    {
        UpdateSession();
        if (GetSystemService(NotificationService) is NotificationManager manager)
            manager.Notify(NotificationId, BuildNotification());
    }

    /// <summary>
    /// Asks for focus so another music app starting will pause us, and so a call
    /// will. <c>SetWillPauseWhenDucked(true)</c> stops Android lowering the mix for
    /// a notification sound on our behalf; <see cref="OnFocusChange"/> then decides.
    /// </summary>
    private void RequestFocus()
    {
        if (GetSystemService(AudioService) is not AudioManager audio) return;
        _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)!
            .SetWillPauseWhenDucked(true)!
            .SetOnAudioFocusChangeListener(new FocusListener())!
            .Build();
        audio.RequestAudioFocus(_focusRequest!);
    }

    private static void OnFocusChange(AudioFocus change)
    {
        switch (change)
        {
            case AudioFocus.Loss:
                // Another app started playing music. Stay paused until asked.
                _pausedForCall = false;
                PauseFromSystem();
                break;
            case AudioFocus.LossTransient when InCall():
                if (_watched?.PauseAll() == true)
                {
                    _held = true;
                    _pausedForCall = true;
                }
                break;
            case AudioFocus.Gain when _pausedForCall:
                ResumeFromSystem();
                break;
            // May-duck, and transient losses that are not a call (a voice note, a
            // video, a navigation prompt, a text alert): keep playing at full volume.
        }
    }

    private static bool InCall() =>
        global::Android.App.Application.Context.GetSystemService(AudioService) is AudioManager audio
        && audio.Mode is Mode.Ringtone or Mode.InCall or Mode.InCommunication;

    private void UpdateSession()
    {
        if (_session is null) return;
        bool playing = IsPlaying;
        _session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause)!
            .SetState(playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                      PlaybackState.PlaybackPositionUnknown, 1f)!
            .Build());
        _session.SetMetadata(new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, string.IsNullOrEmpty(_text) ? "MKB Mixer" : _text)!
            .PutString(MediaMetadata.MetadataKeyArtist, "MKB Mixer")!
            .Build());
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is null)
            manager.CreateNotificationChannel(
                new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
                {
                    Description = "Shown while a deck is playing"
                });

        // The launcher intent brings the existing task forward rather than stacking
        // a second activity on top of it.
        Intent? open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        PendingIntent? tap = open is null
            ? null
            : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        bool playing = IsPlaying;
        var toggle = new Notification.Action.Builder(
                Icon.CreateWithResource(this, playing
                    ? global::Android.Resource.Drawable.IcMediaPause
                    : global::Android.Resource.Drawable.IcMediaPlay),
                playing ? "Pause" : "Play",
                PendingIntent.GetForegroundService(this, playing ? 1 : 2,
                    new Intent(this, typeof(PlaybackService)).SetAction(playing ? ActionPause : ActionPlay),
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))
            .Build();

        string text = string.IsNullOrEmpty(_text) ? "Playing" : _text;
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("MKB Mixer")!
            .SetContentText(playing ? text : $"Paused · {text}")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetContentIntent(tap)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(Notification.CategoryTransport)!
            .AddAction(toggle)!
            .SetStyle(new Notification.MediaStyle()
                .SetMediaSession(_session?.SessionToken)!
                .SetShowActionsInCompactView(0))!
            .Build()!;
    }

    /// <summary>Lock screen, headset and Bluetooth buttons, and the system media panel.</summary>
    private sealed class SessionCallback : MediaSession.Callback
    {
        public override void OnPlay() => Dispatcher.UIThread.Post(ResumeFromSystem);
        public override void OnPause() => Dispatcher.UIThread.Post(PauseFromSystem);
    }

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) =>
            Dispatcher.UIThread.Post(() => OnFocusChange(focusChange));
    }

    private sealed class NoisyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                Dispatcher.UIThread.Post(PauseFromSystem);
        }
    }
}
```

- [ ] **Step 2: Build for Android**

Run: `dotnet build src/Mkb.Mixer.App.Android -c Debug 2>&1 | tail -20`
Expected: `Build succeeded` with no new errors. Fix binding-name mismatches if the compiler reports any (e.g. an enum member spelled differently in .NET Android); keep the behaviour identical. Then run `dotnet test` to confirm nothing shared broke.

- [ ] **Step 3: Device checklist** (run when a device is connected; otherwise hand this list to the user)

1. Play deck A, lock the phone: the lock screen shows MKB Mixer with a pause button; pausing pauses A; play resumes A only.
2. Wired headphones or a Bluetooth speaker playing the mix: unplug/disconnect → mix pauses; the notification still offers Play for 10 minutes.
3. Disconnect a paired Bluetooth watch (not the audio output) → mix keeps playing.
4. Receive a text while playing → mix keeps playing at full volume.
5. Start another music app → mix pauses and does not resume by itself.
6. Receive a phone call → mix pauses; after hanging up it resumes.
7. Headset play/pause button toggles the mix.

- [ ] **Step 4: Commit**

```bash
git add src/Mkb.Mixer.App.Android/PlaybackService.cs
git commit -m "Add Android media controls and pause for calls, other music and lost outputs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the features**

In the Android section, after the paragraph that ends "The service stops after 10 seconds of silence.", add:

```markdown
The notification, the lock screen and headset or Bluetooth buttons pause and
resume the whole mix. The mix also pauses when another app starts playing music,
during a phone call (resuming afterwards), and when the output it is playing
through disconnects — wired headphones unplugged, or a Bluetooth speaker going
out of range. A text arriving, a voice note, or a Bluetooth device that is not
the audio output (a watch, say) disconnecting does not interrupt it. After one of
those pauses the play button keeps working for 10 minutes.
```

Add a new section before `## If playback is silent`:

```markdown
## Headphone cue

Each deck has a **CUE** button that sends it to the headphones before the
crossfader, so the next track can be lined up without the room hearing it. Pick a
mode in the *headphones* row under the crossfader:

| Mode | Use it when |
|---|---|
| **Off** | No headphones. Normal stereo output. |
| **Split** | One output and a splitter cable — the usual phone setup. The room mix plays in mono on the left channel and the headphones in mono on the right. |
| **Device** | A second output, such as USB headphones or a second sound card. The room mix stays stereo on the main output. |

The *cue — master* slider sets what the headphones hear, from the cued decks alone
to the room mix alone. Most phones can only play through one output at a time, so
Device mode there is best-effort; use Split.

## Shuffle and repeat

**⇄** makes a deck take a random track from its queue, and **↻** puts each played
track back on the end of the queue so it never runs dry. Both are per deck and
apply to the auto-cue as well as to a track simply ending. With both on, the same
song is never picked twice in a row.
```

In the "Added" list under "What changed from the original", append:

```markdown
- Headphone cue, in Split (one output and a splitter) or second-device mode.
- Per-deck shuffle and repeat.
- Android media controls, and pausing for calls, other music apps and lost outputs.
```

- [ ] **Step 2: Final verification**

Run: `dotnet test && dotnet build src/Mkb.Mixer.App.Android -c Debug 2>&1 | tail -3`
Expected: all tests pass; Android build succeeds.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Document headphone cue, shuffle/repeat and the Android media controls

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
