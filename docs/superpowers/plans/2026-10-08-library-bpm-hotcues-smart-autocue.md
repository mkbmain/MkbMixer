# Library history, BPM sync, hot cues and smart auto-cue — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add BPM detection and per-track memory to MkbMixer, and build library sorting/history/BPM filtering, deck BPM sync and nudge, saved hot cues, and an auto-cue that fades on real sound and can match tempo.

**Architecture:** One decode per track feeds a `TrackAnalysis` (waveform, BPM, beat offset, first/last sound) computed by two pure detectors in `Mkb.Mixer.Audio`. Results and per-track user data (hot cues, BPM correction, play history) live in a JSON-backed `TrackStore` in `Mkb.Mixer.Library`. An `AnalysisQueue` in the app runs background analysis for next-up and library tracks; the deck, library and `AutoCue` read the store.

**Tech Stack:** .NET 10, Avalonia 12.1, CommunityToolkit.Mvvm 8.4, System.Text.Json, xunit 2.9 with Avalonia.Headless.

**Spec:** `docs/superpowers/specs/2026-10-08-library-bpm-hotcues-smart-autocue-design.md`

## Global Constraints

- No new NuGet packages. No native dependencies.
- `Mkb.Mixer.Audio` must not reference `Mkb.Mixer.Library` or Avalonia. `Mkb.Mixer.Library` may reference Audio (it already does).
- Track store file: `tracks.json` in the same folder as `settings.json` (`%AppData%/MkbMixer/`). Tests never touch it: they use `TrackStore.InMemory()` or a temp path.
- Detection runs on mono audio decimated to ~11025 Hz (`AnalysisSignal.TargetRate`).
- Silence: −45 dBFS in 50 ms windows; fade-out end at −30 dBFS if within the final 10 s.
- Tempo: search 70–180 BPM; fold into [87.5, 175); confidence gate 0.1.
- SYNC clamps tempo to 0.5–1.5. Nudge is ±4% while held. Tempo match only within ±8%. Glide back takes 8 s.
- Hot cues: 4 per deck/track. "Played" means position ≥ min(30 s, 90% of duration) while playing, or the track ended.
- Recently played lists at most 100 tracks. The ≈ filter is the audible deck's BPM ±6%.
- New settings: `AnalyseLibraryBpm` (`bool?`, null = platform default: on unless `App.UsePhoneLayout`), `AutoCueTempoMatch` (`TempoMatchMode`, default `Off`).
- Commit messages follow the repo style: a plain sentence summary, no `feat:` prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- The App project has no implicit usings: add `using` lines explicitly there. The test and Library/Audio projects do have them.
- Run tests with `dotnet test tests/Mkb.Mixer.Tests` (add `--filter "FullyQualifiedName~ClassName"` for one class).

## Review Focus

1. **A file that cannot be decoded during background analysis** — the worker must carry on with the rest of the folder and must not cache an empty result. Test: `AnalysisQueueTests.AFailingFileDoesNotStopTheQueue` and `EmptyResultIsNotCached` (Task 5).
2. **The held next-up track being removed from the playlist** — auto-cue must never load a track the user just removed. Test: `LookAheadTests.RemovingTheHeldTrackPicksAgain` (Task 12).
3. **A hot cue set at 0:00** — slot must count as set and jump back to the start, not be treated as empty. Test: `HotCueTests.ACueAtTheVeryStartCountsAsSet` (Task 10).
4. **The user seeks past the outgoing track's last sound with auto-cue on** — fade should start at once, with the fader starting from where it is. Test: `AutoCueTimingTests.SeekingPastTheLastSoundStartsTheFadeAtOnce` (Task 13).
5. **BPM range typed backwards (min > max)** — treat it as the same range the other way round, not as "nothing matches". Test: `LibraryFilterTests.ABackwardsRangeStillFilters` (Task 9).

---

## File Map

**Create**

| File | Responsibility |
|---|---|
| `src/Mkb.Mixer.Audio/AnalysisSignal.cs` | Mono, decimated copy of decoded audio for the detectors |
| `src/Mkb.Mixer.Audio/SilenceDetector.cs` | First and last real sound |
| `src/Mkb.Mixer.Audio/TempoDetector.cs` | BPM and first-beat offset |
| `src/Mkb.Mixer.Audio/TrackAnalysis.cs` | One decode's results |
| `src/Mkb.Mixer.Audio/TrackTiming.cs` | Start/end/BPM that `AutoCue` needs, plus `TempoMatchMode` |
| `src/Mkb.Mixer.Library/TrackInfo.cs` | Persisted per-track record and `FileStamp` |
| `src/Mkb.Mixer.Library/TrackStore.cs` | Loads/saves `tracks.json` |
| `src/Mkb.Mixer.App/Services/AnalysisQueue.cs` | Deck analysis and background worker |
| `src/Mkb.Mixer.App/ViewModels/LibraryRow.cs` | One library grid row |
| `src/Mkb.Mixer.App/ViewModels/HotCueSlot.cs` | One hot cue button |
| `src/Mkb.Mixer.App/Controls/HotCuePalette.cs` | The four cue colours, shared by buttons and waveform |
| `src/Mkb.Mixer.App/Converters/HotCueBrushConverter.cs` | Slot index → brush |
| `tests/Mkb.Mixer.Tests/Synth.cs` | Synthetic audio for detector tests |
| `tests/Mkb.Mixer.Tests/SilenceDetectorTests.cs`, `TempoDetectorTests.cs`, `TrackStoreTests.cs`, `AnalysisQueueTests.cs`, `LibraryRowTests.cs`, `PlayHistoryTests.cs`, `DeckBpmTests.cs`, `LibraryFilterTests.cs`, `HotCueTests.cs`, `LookAheadTests.cs`, `AutoCueTimingTests.cs`, `TempoMatchTests.cs` | Tests per task |

**Modify**

| File | Change |
|---|---|
| `src/Mkb.Mixer.Audio/IAudioEngine.cs`, `SoundFlowAudioEngine.cs` | `AnalyseAsync` returns `TrackAnalysis` |
| `src/Mkb.Mixer.Audio/AutoCue.cs` | Timing-aware trigger and start, tempo fix, tempo match and glide |
| `src/Mkb.Mixer.Library/Settings.cs` | Two new settings |
| `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` | Track-change detection, played event, BPM/SYNC/nudge, hot cues, look-ahead, tempo echo |
| `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` | Store/queue wiring, library rows, history, filter, tempo-match setting |
| `src/Mkb.Mixer.App/ViewModels/FolderNode.cs` | "Recently played" node |
| `src/Mkb.Mixer.App/Controls/WaveformView.cs` | Hot cue markers |
| `src/Mkb.Mixer.App/Views/DeckView.axaml(.cs)` | BPM/SYNC/nudge/hot cue row |
| `src/Mkb.Mixer.App/Views/MainView.axaml`, `PhoneView.axaml` | Library columns, filter, match-tempo combo |
| `src/Mkb.Mixer.App/Styles.axaml`, `App.axaml` | Styles and converter resource |
| `tests/Mkb.Mixer.Tests/FakeAudioEngine.cs`, `FakeDeck.cs`, `UiSmokeTests.cs`, `AudioEngineIntegrationTests.cs` | Fakes and renders |
| `README.md` | Document the features |

---

### Task 1: Analysis signal and silence detection

**Files:**
- Create: `src/Mkb.Mixer.Audio/AnalysisSignal.cs`, `src/Mkb.Mixer.Audio/SilenceDetector.cs`, `tests/Mkb.Mixer.Tests/Synth.cs`
- Test: `tests/Mkb.Mixer.Tests/SilenceDetectorTests.cs`

**Interfaces:**
- Produces: `readonly record struct AnalysisSignal(float[] Samples, int SampleRate)` with `const int TargetRate = 11025` and `static AnalysisSignal From(ReadOnlySpan<float> interleaved, int channels, int sampleRate)`; `static class SilenceDetector` with `static (TimeSpan? FirstSound, TimeSpan? LastSound) Detect(AnalysisSignal signal)`; test helper `static class Synth` (used by Tasks 2 and 3).

- [ ] **Step 1: Write the synth helper and failing tests**

`tests/Mkb.Mixer.Tests/Synth.cs`:

```csharp
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>Generates test audio with known tempo and levels, so detectors can be checked exactly.</summary>
internal static class Synth
{
    public const int Rate = AnalysisSignal.TargetRate;

    public static float[] Silence(double seconds, int rate = Rate) => new float[(int)(seconds * rate)];

    /// <summary>A 440 Hz sine whose RMS level is <paramref name="db"/> dBFS.</summary>
    public static float[] Tone(double seconds, double db, int rate = Rate)
    {
        var s = new float[(int)(seconds * rate)];
        double amp = Math.Pow(10, db / 20) * Math.Sqrt(2);
        for (int i = 0; i < s.Length; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * 440 * i / rate));
        return s;
    }

    /// <summary>A 440 Hz sine fading linearly in dB.</summary>
    public static float[] Fade(double seconds, double fromDb, double toDb, int rate = Rate)
    {
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++)
        {
            double db = fromDb + (toDb - fromDb) * i / s.Length;
            s[i] = (float)(Math.Pow(10, db / 20) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 440 * i / rate));
        }
        return s;
    }

    public static float[] Noise(double seconds, double amplitude, int seed = 1, int rate = Rate)
    {
        var random = new Random(seed);
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        return s;
    }

    /// <summary>A short decaying 1 kHz click on every beat, optionally over white noise.</summary>
    public static float[] Clicks(double bpm, double seconds, double offset = 0, double noise = 0, int seed = 1, int rate = Rate)
    {
        float[] s = noise > 0 ? Noise(seconds, noise, seed, rate) : Silence(seconds, rate);
        int clickLength = (int)(0.02 * rate);
        for (int k = 0; ; k++)
        {
            int start = (int)Math.Round((offset + k * 60.0 / bpm) * rate);
            if (start >= s.Length) break;
            for (int j = 0; j < clickLength && start + j < s.Length; j++)
                s[start + j] += (float)(0.8 * Math.Exp(-j / (0.005 * rate)) * Math.Sin(2 * Math.PI * 1000 * j / rate));
        }
        return s;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Duplicates a mono buffer into interleaved stereo.</summary>
    public static float[] Stereo(float[] mono)
    {
        var s = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) s[2 * i] = s[2 * i + 1] = mono[i];
        return s;
    }

    public static AnalysisSignal Signal(float[] mono) => new(mono, Rate);
}
```

`tests/Mkb.Mixer.Tests/SilenceDetectorTests.cs`:

```csharp
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class SilenceDetectorTests
{
    [Fact]
    public void DownmixesAndDecimatesToTheAnalysisRate()
    {
        var stereo = Enumerable.Repeat(0.5f, 44100 * 2).ToArray();

        AnalysisSignal signal = AnalysisSignal.From(stereo, channels: 2, sampleRate: 44100);

        Assert.Equal(11025, signal.SampleRate);
        Assert.Equal(11025, signal.Samples.Length);
        Assert.All(signal.Samples, s => Assert.Equal(0.5f, s, 4));
    }

    [Fact]
    public void FindsLeadingAndTrailingSilence()
    {
        float[] audio = Synth.Concat(Synth.Silence(2), Synth.Tone(10, -10), Synth.Silence(3));

        var (first, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(2.0, first!.Value.TotalSeconds, 0.05);
        Assert.Equal(12.0, last!.Value.TotalSeconds, 0.05);
    }

    [Fact]
    public void AQuietFadeOutCountsAsTheEnd()
    {
        // -10 dB for 10 s, then down to -60 dB over 8 s: it crosses -30 dB at 13.2 s
        // and -45 dB at 15.6 s. The near-silent tail should not hold up a mix.
        float[] audio = Synth.Concat(Synth.Tone(10, -10), Synth.Fade(8, -10, -60), Synth.Silence(2));

        var (_, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(13.2, last!.Value.TotalSeconds, 0.1);
    }

    [Fact]
    public void ALongQuietOutroIsKept()
    {
        // 20 s at -38 dB is quiet music, not a fade, so it is not cut.
        float[] audio = Synth.Concat(Synth.Tone(10, -10), Synth.Tone(20, -38), Synth.Silence(2));

        var (_, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(30.0, last!.Value.TotalSeconds, 0.05);
    }

    [Fact]
    public void DigitalSilenceHasNoSound()
    {
        var (first, last) = SilenceDetector.Detect(Synth.Signal(Synth.Silence(5)));

        Assert.Null(first);
        Assert.Null(last);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~SilenceDetectorTests"`
Expected: build error, `AnalysisSignal` and `SilenceDetector` do not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Audio/AnalysisSignal.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>
/// A mono, decimated copy of a decoded track for the detectors. Tempo and silence
/// need nothing above a few kHz, and working at a quarter of the rate keeps
/// detection to a few tens of milliseconds on top of the decode.
/// </summary>
public readonly record struct AnalysisSignal(float[] Samples, int SampleRate)
{
    public const int TargetRate = 11025;

    /// <summary>Averages the channels, then averages blocks of samples down to about <see cref="TargetRate"/>.</summary>
    public static AnalysisSignal From(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int factor = Math.Max(1, (int)Math.Round((double)sampleRate / TargetRate));
        int block = factor * channels;
        var mono = new float[interleaved.Length / block];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            ReadOnlySpan<float> slice = interleaved.Slice(i * block, block);
            foreach (float s in slice) sum += s;
            mono[i] = sum / block;
        }
        return new AnalysisSignal(mono, sampleRate / factor);
    }
}
```

`src/Mkb.Mixer.Audio/SilenceDetector.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>Finds where a track's real sound starts and ends.</summary>
/// <remarks>
/// Levels are measured as RMS over 50 ms windows, so a single click or a hiss tail
/// does not count as sound. The end is pulled back to where a fade-out drops below
/// <see cref="FadeEndDb"/> if that happens within the final <see cref="FadeSearch"/>,
/// so a long near-silent tail does not leave dead air in a mix; a long quiet outro
/// further back than that is music and is kept.
/// </remarks>
public static class SilenceDetector
{
    public const double SilenceDb = -45;
    public const double FadeEndDb = -30;
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan FadeSearch = TimeSpan.FromSeconds(10);

    public static (TimeSpan? FirstSound, TimeSpan? LastSound) Detect(AnalysisSignal signal)
    {
        int window = Math.Max(1, (int)(signal.SampleRate * Window.TotalSeconds));
        int count = signal.Samples.Length / window;
        if (count == 0) return (null, null);

        var db = new double[count];
        for (int w = 0; w < count; w++)
        {
            double sum = 0;
            for (int i = w * window; i < (w + 1) * window; i++)
                sum += (double)signal.Samples[i] * signal.Samples[i];
            double rms = Math.Sqrt(sum / window);
            db[w] = rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
        }

        int first = Array.FindIndex(db, d => d > SilenceDb);
        if (first < 0) return (null, null);
        int last = Array.FindLastIndex(db, d => d > SilenceDb);
        int loud = Array.FindLastIndex(db, d => d > FadeEndDb);
        int searchWindows = (int)(FadeSearch / Window);
        int end = loud >= 0 && last - loud <= searchWindows ? loud : last;

        return (At(first), At(end + 1));

        TimeSpan At(int w) => TimeSpan.FromSeconds((double)w * window / signal.SampleRate);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~SilenceDetectorTests"`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.Audio/AnalysisSignal.cs src/Mkb.Mixer.Audio/SilenceDetector.cs tests/Mkb.Mixer.Tests/Synth.cs tests/Mkb.Mixer.Tests/SilenceDetectorTests.cs
git commit -m "Detect where a track's real sound starts and ends

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Tempo detection

**Files:**
- Create: `src/Mkb.Mixer.Audio/TempoDetector.cs`
- Test: `tests/Mkb.Mixer.Tests/TempoDetectorTests.cs`

**Interfaces:**
- Consumes: `AnalysisSignal`, `Synth` (Task 1).
- Produces: `static class TempoDetector` with `static (double? Bpm, TimeSpan? BeatOffset) Detect(AnalysisSignal signal)` and constants `MinBpm = 70`, `MaxBpm = 180`, `FoldLow = 87.5`, `FoldHigh = 175`, `MinConfidence = 0.1`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/TempoDetectorTests.cs`:

```csharp
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class TempoDetectorTests
{
    [Theory]
    [InlineData(90)] [InlineData(120)] [InlineData(128)] [InlineData(174)]
    public void FindsTheTempoOfAClickTrack(double bpm)
    {
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(bpm, seconds: 30)));

        Assert.NotNull(detected);
        Assert.Equal(bpm, detected!.Value, 0.5);
    }

    [Fact]
    public void FindsTheTempoUnderNoise()
    {
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(128, seconds: 30, noise: 0.05)));

        Assert.Equal(128, detected!.Value, 0.5);
    }

    [Fact]
    public void FoldsSlowTempoIntoTheDisplayedOctave()
    {
        // Autocorrelation cannot tell 75 from 150; the user corrects it with x1/2.
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(75, seconds: 30)));

        Assert.Equal(150, detected!.Value, 0.5);
    }

    [Fact]
    public void FindsWhereTheFirstBeatFalls()
    {
        var (_, offset) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(120, seconds: 30, offset: 0.25)));

        Assert.Equal(0.25, offset!.Value.TotalSeconds, 0.03);
    }

    [Fact]
    public void NoiseHasNoTempo()
    {
        var (detected, offset) = TempoDetector.Detect(Synth.Signal(Synth.Noise(30, 0.3)));

        Assert.Null(detected);
        Assert.Null(offset);
    }

    [Fact]
    public void SilenceHasNoTempo() =>
        Assert.Null(TempoDetector.Detect(Synth.Signal(Synth.Silence(30))).Bpm);

    [Fact]
    public void ATooShortClipHasNoTempo() =>
        Assert.Null(TempoDetector.Detect(Synth.Signal(Synth.Clicks(120, seconds: 3))).Bpm);
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TempoDetectorTests"`
Expected: build error, `TempoDetector` does not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Audio/TempoDetector.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>Estimates a track's tempo and where its first beat falls.</summary>
/// <remarks>
/// Builds an onset envelope (frame-to-frame rises in log energy, which picks out
/// drum hits), autocorrelates it, then scores each candidate BPM on its first
/// <see cref="Harmonics"/> beat multiples. Scoring the multiples sharpens the
/// estimate to well under one 10 ms frame. The result is folded into one octave
/// because autocorrelation cannot tell 75 from 150; the deck's x1/2 and x2 fix
/// the rare track that lands in the wrong one.
/// </remarks>
public static class TempoDetector
{
    public const double MinBpm = 70, MaxBpm = 180;

    /// <summary>Results are folded into [FoldLow, FoldHigh), which keeps 174 BPM drum and bass and 90 BPM hip-hop as they are.</summary>
    public const double FoldLow = 87.5, FoldHigh = 175;

    /// <summary>
    /// Below this the strongest periodicity is too weak to trust. A wrong BPM is
    /// worse than none, because SYNC and the auto-cue act on it.
    /// </summary>
    public const double MinConfidence = 0.1;

    private const int Harmonics = 8;
    private const int FramesPerSecond = 100;

    public static (double? Bpm, TimeSpan? BeatOffset) Detect(AnalysisSignal signal)
    {
        int hop = Math.Max(1, signal.SampleRate / FramesPerSecond);
        double frameRate = (double)signal.SampleRate / hop;
        float[] onset = OnsetEnvelope(signal.Samples, hop);

        int maxLag = (int)Math.Ceiling(60 * frameRate / MinBpm * Harmonics) + 2;
        if (onset.Length < maxLag * 2) return (null, null);

        double[] ac = Autocorrelate(onset, maxLag);
        if (ac[0] <= 1e-12) return (null, null);

        double coarse = MinBpm, coarseScore = double.MinValue;
        for (double bpm = MinBpm; bpm <= MaxBpm; bpm += 0.5)
        {
            double s = Score(ac, frameRate, bpm);
            if (s > coarseScore) { coarseScore = s; coarse = bpm; }
        }

        double folded = Fold(coarse);
        double best = folded, bestScore = double.MinValue;
        for (double bpm = folded - 0.6; bpm <= folded + 0.6; bpm += 0.01)
        {
            double s = Score(ac, frameRate, bpm);
            if (s > bestScore) { bestScore = s; best = bpm; }
        }

        if (bestScore / (Harmonics * ac[0]) < MinConfidence) return (null, null);

        return (Math.Round(best, 2), Phase(onset, frameRate, best));
    }

    private static double Fold(double bpm)
    {
        while (bpm < FoldLow) bpm *= 2;
        while (bpm >= FoldHigh) bpm /= 2;
        return bpm;
    }

    /// <summary>Positive rises in log energy per 10 ms frame, with the mean removed.</summary>
    private static float[] OnsetEnvelope(float[] samples, int hop)
    {
        int window = hop * 2;
        int frames = (samples.Length - window) / hop;
        if (frames < 2) return [];

        var energy = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int start = f * hop;
            for (int i = start; i < start + window; i++) sum += (double)samples[i] * samples[i];
            energy[f] = Math.Log10(1e-6 + sum / window);
        }

        var onset = new float[frames];
        double mean = 0;
        for (int f = 1; f < frames; f++)
        {
            onset[f] = (float)Math.Max(0, energy[f] - energy[f - 1]);
            mean += onset[f];
        }
        mean /= frames;
        for (int f = 0; f < frames; f++) onset[f] -= (float)mean;
        return onset;
    }

    /// <summary>Autocorrelation normalised by overlap length, so long lags are not penalised.</summary>
    private static double[] Autocorrelate(float[] x, int maxLag)
    {
        var ac = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < x.Length; i++) sum += (double)x[i] * x[i + lag];
            ac[lag] = sum / (x.Length - lag);
        }
        return ac;
    }

    private static double Score(double[] ac, double frameRate, double bpm)
    {
        double period = 60 * frameRate / bpm;
        double sum = 0;
        for (int k = 1; k <= Harmonics; k++) sum += At(ac, period * k);
        return sum;
    }

    /// <summary>Linear interpolation between lags.</summary>
    private static double At(double[] ac, double lag)
    {
        int i = (int)lag;
        if (i + 1 >= ac.Length) return 0;
        double f = lag - i;
        return ac[i] * (1 - f) + ac[i + 1] * f;
    }

    /// <summary>The offset within one beat that lines up best with the onsets.</summary>
    private static TimeSpan Phase(float[] onset, double frameRate, double bpm)
    {
        double period = 60 * frameRate / bpm;
        int best = 0;
        double bestSum = double.MinValue;
        for (int phase = 0; phase < (int)period; phase++)
        {
            double sum = 0;
            for (double t = phase; ; t += period)
            {
                int i = (int)Math.Round(t);
                if (i >= onset.Length) break;
                sum += onset[i];
            }
            if (sum > bestSum) { bestSum = sum; best = phase; }
        }
        return TimeSpan.FromSeconds(best / frameRate);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TempoDetectorTests"`
Expected: 10 passed (the theory counts four).

If the 174 case misses by more than 0.5, the refinement is landing on a whole-frame lag: check `Score` uses `period * k` (fractional) and `Harmonics` is 8. If noise is wrongly given a BPM, print `bestScore / (Harmonics * ac[0])` for the noise and the noisy-clicks cases, and set `MinConfidence` midway between them; record both values in the commit message.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.Audio/TempoDetector.cs tests/Mkb.Mixer.Tests/TempoDetectorTests.cs
git commit -m "Detect tempo and first-beat offset from an onset envelope

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: TrackAnalysis from one decode

**Files:**
- Create: `src/Mkb.Mixer.Audio/TrackAnalysis.cs`
- Modify: `src/Mkb.Mixer.Audio/IAudioEngine.cs:53-54`, `src/Mkb.Mixer.Audio/SoundFlowAudioEngine.cs:424-453`, `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs:95-99`, `tests/Mkb.Mixer.Tests/FakeAudioEngine.cs`, `tests/Mkb.Mixer.Tests/AudioEngineIntegrationTests.cs:104-111`
- Test: `tests/Mkb.Mixer.Tests/TempoDetectorTests.cs` (one more test), `tests/Mkb.Mixer.Tests/AudioEngineIntegrationTests.cs`

**Interfaces:**
- Consumes: `AnalysisSignal`, `SilenceDetector`, `TempoDetector`, `Waveform`.
- Produces: `sealed record TrackAnalysis(Waveform Waveform, double? Bpm, TimeSpan? BeatOffset, TimeSpan? FirstSound, TimeSpan? LastSound)` with `static TrackAnalysis Empty` and `static TrackAnalysis FromSamples(ReadOnlySpan<float> interleaved, int channels, int sampleRate)`. `IAudioEngine.AnalyseAsync(string path, CancellationToken ct = default)` now returns `Task<TrackAnalysis>`; it returns `TrackAnalysis.Empty` (same instance) when the file cannot be decoded. `FakeAudioEngine` gains `Dictionary<string, TrackAnalysis> Analyses`, `HashSet<string> Throwing`, `List<string> AnalysedPaths`, and `static TrackAnalysis Analysis(double? bpm = null, double? first = null, double? last = null)`.

- [ ] **Step 1: Write the failing tests**

Append to `TempoDetectorTests`:

```csharp
    [Fact]
    public void OneDecodeGivesWaveformTempoAndSilence()
    {
        float[] mono = Synth.Concat(Synth.Silence(1, 44100), Synth.Clicks(120, 30, rate: 44100), Synth.Silence(2, 44100));

        TrackAnalysis a = TrackAnalysis.FromSamples(Synth.Stereo(mono), channels: 2, sampleRate: 44100);

        Assert.Equal(Waveform.DefaultBuckets, a.Waveform.Peaks.Length);
        Assert.Equal(120, a.Bpm!.Value, 0.5);
        Assert.Equal(1.0, a.FirstSound!.Value.TotalSeconds, 0.05);
        Assert.InRange(a.LastSound!.Value.TotalSeconds, 30.5, 31.05);
    }
```

In `AudioEngineIntegrationTests`, replace the analysis test body (line 107 onwards) with:

```csharp
        using var engine = new SoundFlowAudioEngine();
        TrackAnalysis analysis = await engine.AnalyseAsync(WriteTone(seconds: 2));

        Assert.Equal(Waveform.DefaultBuckets, analysis.Waveform.Peaks.Length);
        Assert.Contains(analysis.Waveform.Peaks, p => p > 0.2f);
        Assert.Equal(0, analysis.FirstSound!.Value.TotalSeconds, 0.05);
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TempoDetectorTests|FullyQualifiedName~AudioEngineIntegrationTests"`
Expected: build error, `TrackAnalysis` does not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Audio/TrackAnalysis.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>Everything one decode of a track tells us.</summary>
/// <param name="BeatOffset">Where the first beat falls. Stored for phase sync; nothing uses it yet.</param>
public sealed record TrackAnalysis(
    Waveform Waveform,
    double? Bpm,
    TimeSpan? BeatOffset,
    TimeSpan? FirstSound,
    TimeSpan? LastSound)
{
    /// <summary>The file could not be decoded. Compared by reference, so never cached.</summary>
    public static TrackAnalysis Empty { get; } = new(Waveform.Empty, null, null, null, null);

    public static TrackAnalysis FromSamples(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        Waveform wave = Waveform.FromSamples(interleaved, channels, Waveform.DefaultBuckets);
        AnalysisSignal signal = AnalysisSignal.From(interleaved, channels, sampleRate);
        var (bpm, offset) = TempoDetector.Detect(signal);
        var (first, last) = SilenceDetector.Detect(signal);
        return new TrackAnalysis(wave, bpm, offset, first, last);
    }
}
```

`IAudioEngine.cs` — replace the last member:

```csharp
    /// <summary>
    /// Decodes a file once for its waveform, tempo and start/end points, off the
    /// calling thread. Returns <see cref="TrackAnalysis.Empty"/> if it cannot be decoded.
    /// </summary>
    Task<TrackAnalysis> AnalyseAsync(string path, CancellationToken ct = default);
```

`SoundFlowAudioEngine.cs` — change the signature to `public Task<TrackAnalysis> AnalyseAsync(string path, CancellationToken ct = default) =>`, `if (_engine is null) return TrackAnalysis.Empty;`, the return to:

```csharp
                return TrackAnalysis.FromSamples(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples),
                    Format.Channels,
                    Format.SampleRate);
```

and the catch-all to `return TrackAnalysis.Empty;`.

`DeckViewModel.cs` — in `LoadAsync`, replace the two lines inside `try`:

```csharp
            TrackAnalysis analysis = await _engine.AnalyseAsync(track.Path, ct);
            if (!ct.IsCancellationRequested) Waveform = analysis.Waveform;
```

`FakeAudioEngine.cs` — replace `AnalyseAsync` with:

```csharp
    /// <summary>Per-path results. Anything not listed gets <see cref="Analysis"/> with no BPM.</summary>
    public Dictionary<string, TrackAnalysis> Analyses { get; } = [];

    /// <summary>Paths whose analysis throws, as a broken decoder might.</summary>
    public HashSet<string> Throwing { get; } = [];

    /// <summary>Every path analysed, in order.</summary>
    public List<string> AnalysedPaths { get; } = [];

    /// <summary>A recognisable synthetic waveform, so renders are deterministic, plus the given numbers.</summary>
    public static TrackAnalysis Analysis(double? bpm = null, double? first = null, double? last = null)
    {
        var peaks = new float[Waveform.DefaultBuckets];
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = 0.35f + 0.55f * MathF.Abs(MathF.Sin(i * 0.012f)) * (0.6f + 0.4f * MathF.Sin(i * 0.0013f));
        return new TrackAnalysis(new Waveform(peaks), bpm, bpm is null ? null : TimeSpan.Zero,
            first is null ? null : TimeSpan.FromSeconds(first.Value),
            last is null ? null : TimeSpan.FromSeconds(last.Value));
    }

    public Task<TrackAnalysis> AnalyseAsync(string path, CancellationToken ct = default)
    {
        lock (AnalysedPaths) AnalysedPaths.Add(path);
        if (Throwing.Contains(path)) throw new InvalidOperationException($"cannot decode {path}");
        return Task.FromResult(Analyses.TryGetValue(path, out TrackAnalysis? a) ? a : Analysis());
    }
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass (the integration test may be skipped/failing only if it already was on this machine; compare with `git stash; dotnet test; git stash pop` if unsure).

- [ ] **Step 5: Commit**

```bash
git add -A src/Mkb.Mixer.Audio src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs tests/Mkb.Mixer.Tests
git commit -m "Analyse tempo and start/end points in the same decode as the waveform

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Per-track store

**Files:**
- Create: `src/Mkb.Mixer.Library/TrackInfo.cs`, `src/Mkb.Mixer.Library/TrackStore.cs`
- Test: `tests/Mkb.Mixer.Tests/TrackStoreTests.cs`

**Interfaces:**
- Consumes: `TrackAnalysis` (Task 3).
- Produces:
  - `readonly record struct FileStamp(long Size, DateTime WriteUtc)` with `static FileStamp Missing` and `static FileStamp Of(string path)`.
  - `sealed record TrackInfo` with init properties `long FileSize`, `DateTime FileWriteUtc`, `bool IsAnalysed`, `double? Bpm`, `double? BeatOffsetSeconds`, `double? FirstSoundSeconds`, `double? LastSoundSeconds`, `double BpmMultiplier = 1`, `double?[] HotCueSeconds` (length `TrackInfo.HotCueCount` = 4), `DateTime? LastPlayedUtc`, `int PlayCount`; computed `double? DisplayBpm`; methods `TrackInfo WithoutAnalysis()`, `TrackInfo WithHotCue(int slot, double? seconds)`.
  - `sealed class TrackStore : IDisposable` with `TrackStore(string? path, TimeSpan? saveDelay = null)`, `static TrackStore InMemory()`, `static TrackStore Default()`, `TrackInfo? Get(string path)`, `void Update(string path, Func<TrackInfo, TrackInfo> change)`, `void SetAnalysis(string path, TrackAnalysis analysis)`, `void MarkPlayed(string path, DateTime utcNow)`, `IReadOnlyList<string> RecentlyPlayed(int max)`, `void Flush()`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/TrackStoreTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TrackStoreTests"`
Expected: build error, `TrackStore` does not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Library/TrackInfo.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Mkb.Mixer.Library;

/// <summary>A file's size and modified time, so a changed file is re-analysed rather than trusted.</summary>
public readonly record struct FileStamp(long Size, DateTime WriteUtc)
{
    public static FileStamp Missing { get; } = new(-1, DateTime.MinValue);

    public static FileStamp Of(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return Missing;
        }
    }
}

/// <summary>What the app remembers about one track between runs.</summary>
public sealed record TrackInfo
{
    public const int HotCueCount = 4;

    public long FileSize { get; init; } = -1;
    public DateTime FileWriteUtc { get; init; }

    /// <summary>True once analysed, even if no BPM was found, so the track is not analysed again.</summary>
    public bool IsAnalysed { get; init; }
    public double? Bpm { get; init; }
    public double? BeatOffsetSeconds { get; init; }
    public double? FirstSoundSeconds { get; init; }
    public double? LastSoundSeconds { get; init; }

    /// <summary>The user's x1/2 or x2 correction. Kept when the track is re-analysed.</summary>
    public double BpmMultiplier { get; init; } = 1;

    /// <summary>One entry per slot, null when the slot is empty. 0 is a real cue at the very start.</summary>
    public double?[] HotCueSeconds { get; init; } = new double?[HotCueCount];

    public DateTime? LastPlayedUtc { get; init; }
    public int PlayCount { get; init; }

    [JsonIgnore] public double? DisplayBpm => Bpm * BpmMultiplier;
    [JsonIgnore] public FileStamp Stamp => new(FileSize, FileWriteUtc);

    public TrackInfo WithoutAnalysis() => this with
    {
        IsAnalysed = false, Bpm = null, BeatOffsetSeconds = null,
        FirstSoundSeconds = null, LastSoundSeconds = null
    };

    public TrackInfo WithHotCue(int slot, double? seconds)
    {
        var cues = (double?[])HotCueSeconds.Clone();
        cues[slot] = seconds;
        return this with { HotCueSeconds = cues };
    }
}
```

`src/Mkb.Mixer.Library/TrackStore.cs`:

```csharp
using System.Text.Json;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>
/// Per-track memory — analysis results, hot cues, BPM corrections and play
/// history — kept as one JSON file keyed by full path.
/// </summary>
/// <remarks>
/// Held in memory and written about two seconds after the last change, so a burst
/// of background analysis is one write, not hundreds. Writes go to a temp file
/// and are moved over the real one, so a crash mid-write cannot corrupt it. A
/// corrupt or unreadable file starts the store empty rather than stopping the app,
/// as <see cref="SettingsStore"/> does.
/// </remarks>
public sealed class TrackStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string? _path;
    private readonly TimeSpan _saveDelay;
    private readonly Dictionary<string, TrackInfo> _tracks;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Timer? _saveTimer;
    private bool _dirty;

    /// <param name="path">Where to save, or null to keep everything in memory.</param>
    public TrackStore(string? path, TimeSpan? saveDelay = null)
    {
        _path = path;
        _saveDelay = saveDelay ?? TimeSpan.FromSeconds(2);
        _tracks = Load(path);
        if (path is not null)
            _saveTimer = new Timer(_ => Flush());
    }

    public static TrackStore InMemory() => new(null);

    /// <summary>Beside settings.json in the per-user config directory.</summary>
    public static TrackStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MkbMixer", "tracks.json"));

    /// <summary>
    /// What is known about a track. If the file has changed since it was analysed,
    /// the analysis is left out but hot cues and history are kept.
    /// </summary>
    public TrackInfo? Get(string path)
    {
        TrackInfo? info;
        lock (_gate) _tracks.TryGetValue(path, out info);
        if (info is null || !info.IsAnalysed) return info;
        return info.Stamp == FileStamp.Of(path) ? info : info.WithoutAnalysis();
    }

    public void Update(string path, Func<TrackInfo, TrackInfo> change)
    {
        lock (_gate)
        {
            TrackInfo current = _tracks.TryGetValue(path, out TrackInfo? existing) ? existing : new TrackInfo();
            _tracks[path] = change(current);
            _dirty = true;
        }
        _saveTimer?.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    public void SetAnalysis(string path, TrackAnalysis analysis)
    {
        FileStamp stamp = FileStamp.Of(path);
        Update(path, i => i with
        {
            FileSize = stamp.Size,
            FileWriteUtc = stamp.WriteUtc,
            IsAnalysed = true,
            Bpm = analysis.Bpm,
            BeatOffsetSeconds = analysis.BeatOffset?.TotalSeconds,
            FirstSoundSeconds = analysis.FirstSound?.TotalSeconds,
            LastSoundSeconds = analysis.LastSound?.TotalSeconds
        });
    }

    public void MarkPlayed(string path, DateTime utcNow) =>
        Update(path, i => i with { LastPlayedUtc = utcNow, PlayCount = i.PlayCount + 1 });

    /// <summary>Paths of the most recently played tracks, newest first.</summary>
    public IReadOnlyList<string> RecentlyPlayed(int max)
    {
        lock (_gate)
            return _tracks
                .Where(kv => kv.Value.LastPlayedUtc is not null)
                .OrderByDescending(kv => kv.Value.LastPlayedUtc)
                .Take(max)
                .Select(kv => kv.Key)
                .ToList();
    }

    /// <summary>Writes now if anything has changed. Safe to call from any thread.</summary>
    public void Flush()
    {
        if (_path is null) return;
        lock (_writeGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_tracks, Options);
                _dirty = false;
            }
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing track memory is not worth crashing over; try again next change.
                lock (_gate) _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Flush();
    }

    private static Dictionary<string, TrackInfo> Load(string? path)
    {
        var empty = new Dictionary<string, TrackInfo>(PathComparer);
        if (path is null || !File.Exists(path)) return empty;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, TrackInfo>>(File.ReadAllText(path), Options);
            if (loaded is null) return empty;
            foreach (var (key, info) in loaded)
                empty[key] = info.HotCueSeconds?.Length == TrackInfo.HotCueCount
                    ? info
                    : info with { HotCueSeconds = Resize(info.HotCueSeconds) };
            return empty;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return empty;
        }

        static double?[] Resize(double?[]? cues)
        {
            var fixedSize = new double?[TrackInfo.HotCueCount];
            if (cues is not null) Array.Copy(cues, fixedSize, Math.Min(cues.Length, fixedSize.Length));
            return fixedSize;
        }
    }
}
```

- [ ] **Step 4: Run to see them pass**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TrackStoreTests"`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.Library/TrackInfo.cs src/Mkb.Mixer.Library/TrackStore.cs tests/Mkb.Mixer.Tests/TrackStoreTests.cs
git commit -m "Remember analysis, hot cues and play history per track

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 5: Analysis queue, and decks that notice every track change

**Files:**
- Create: `src/Mkb.Mixer.App/Services/AnalysisQueue.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (constructor, `LoadAsync`, `Refresh`), `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (constructors, `SaveState()`, `Dispose`)
- Test: `tests/Mkb.Mixer.Tests/AnalysisQueueTests.cs`

**Interfaces:**
- Consumes: `TrackStore`, `TrackAnalysis`, `FakeAudioEngine.Analyses/Throwing/AnalysedPaths` (Tasks 3–4).
- Produces:
  - `namespace Mkb.Mixer.App.Services`: `enum AnalysisPriority { NextUp = 0, Library = 1 }`; `sealed class AnalysisQueue : IDisposable` with `AnalysisQueue(IAudioEngine engine, TrackStore store, Action<Action>? post = null, bool manual = false)`, `TrackStore Store`, `event EventHandler<string>? Analysed` (path; raised via `post`), `int PendingCount`, `Task<TrackAnalysis> AnalyseForDeckAsync(Track track, CancellationToken ct = default)`, `void Prefetch(Track? track)`, `void QueueFolder(IEnumerable<Track> tracks)`, `Task DrainAsync()`.
  - `DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null, AnalysisQueue? analysis = null)`; a private `Task OnTrackChangedAsync()` that later tasks extend; `Refresh()` calls it whenever `IDeck.Track` is no longer the track it last saw (by reference).
  - `MainViewModel(IAudioEngine engine, SettingsStore settingsStore, TrackStore tracks, Action<Action>? post = null, bool manualAnalysis = false)`; the existing two-argument constructor delegates with `TrackStore.InMemory()`; the parameterless one uses `TrackStore.Default()`. `public AnalysisQueue Analysis { get; }`. A private field `_tracks` (the `TrackStore`).

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/AnalysisQueueTests.cs`:

```csharp
using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class AnalysisQueueTests
{
    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static (AnalysisQueue Queue, FakeAudioEngine Engine, TrackStore Store) Manual()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        return (new AnalysisQueue(engine, store, post: a => a(), manual: true), engine, store);
    }

    [Fact]
    public async Task NextUpTracksRunBeforeLibraryTracks()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("l1"), T("l2")]);
        queue.Prefetch(T("n1"));

        await queue.DrainAsync();

        Assert.Equal(["/m/n1.mp3", "/m/l1.mp3", "/m/l2.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task ATrackRequestedTwiceIsAnalysedOnce()
    {
        var (queue, engine, _) = Manual();
        queue.Prefetch(T("a"));
        queue.Prefetch(T("a"));
        queue.QueueFolder([T("a")]);

        await queue.DrainAsync();

        Assert.Single(engine.AnalysedPaths);
    }

    [Fact]
    public async Task CachedTracksAreSkipped()
    {
        var (queue, engine, store) = Manual();
        store.SetAnalysis("/m/a.mp3", FakeAudioEngine.Analysis(120));

        queue.Prefetch(T("a"));
        await queue.DrainAsync();

        Assert.Empty(engine.AnalysedPaths);
    }

    [Fact]
    public async Task OpeningAnotherFolderDropsOnlyLibraryWork()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("l1")]);
        queue.Prefetch(T("n1"));
        queue.QueueFolder([T("l2")]);

        await queue.DrainAsync();

        Assert.Equal(["/m/n1.mp3", "/m/l2.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task AFailingFileDoesNotStopTheQueue()
    {
        var (queue, engine, store) = Manual();
        engine.Throwing.Add("/m/bad.mp3");
        engine.Analyses["/m/good.mp3"] = FakeAudioEngine.Analysis(124);

        queue.QueueFolder([T("bad"), T("good")]);
        await queue.DrainAsync();

        Assert.Null(store.Get("/m/bad.mp3"));
        Assert.Equal(124, store.Get("/m/good.mp3")!.Bpm);
    }

    [Fact]
    public async Task EmptyResultIsNotCached()
    {
        var (queue, engine, store) = Manual();
        engine.Analyses["/m/a.mp3"] = TrackAnalysis.Empty;

        queue.Prefetch(T("a"));
        await queue.DrainAsync();

        Assert.Null(store.Get("/m/a.mp3"));
    }

    [Fact]
    public async Task DeckAnalysisIsStoredAndAnnounced()
    {
        var (queue, engine, store) = Manual();
        engine.Analyses["/m/a.mp3"] = FakeAudioEngine.Analysis(128);
        var announced = new List<string>();
        queue.Analysed += (_, path) => announced.Add(path);

        await queue.AnalyseForDeckAsync(T("a"));

        Assert.Equal(128, store.Get("/m/a.mp3")!.Bpm);
        Assert.Equal(["/m/a.mp3"], announced);
    }

    [Fact]
    public async Task TheWorkerRunsByItself()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a());

        queue.Prefetch(T("a"));
        for (int i = 0; i < 100 && store.Get("/m/a.mp3") is null; i++) await Task.Delay(20);

        Assert.True(store.Get("/m/a.mp3")?.IsAnalysed);
    }

    [Fact]
    public async Task ATrackLoadedBehindTheDecksBackGetsItsWaveform()
    {
        // The auto-cue loads the incoming deck on IDeck directly.
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        await deck.LoadAsync(T("first"));

        fake.Load(T("second"));
        deck.Refresh();

        Assert.Equal("second", deck.NowPlaying);
        Assert.NotSame(Waveform.Empty, deck.Waveform);
    }

    [Fact]
    public void SavingStateFlushesTheTrackStore()
    {
        string dir = Directory.CreateTempSubdirectory("mkb-flush").FullName;
        try
        {
            string tracks = Path.Combine(dir, "tracks.json");
            var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(dir, "settings.json")),
                new TrackStore(tracks, saveDelay: TimeSpan.FromHours(1)), post: a => a(), manualAnalysis: true);
            vm.DeckA.LoadAsync(T("a")).Wait();

            vm.SaveState();

            Assert.True(File.Exists(tracks));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~AnalysisQueueTests"`
Expected: build error, `Mkb.Mixer.App.Services` does not exist.

- [ ] **Step 3: Implement the queue**

`src/Mkb.Mixer.App/Services/AnalysisQueue.cs`:

```csharp
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
/// every file on the UI thread.
/// </remarks>
public sealed class AnalysisQueue : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly Action<Action> _post;
    private readonly bool _manual;
    private readonly Lock _gate = new();
    private readonly List<(string Path, AnalysisPriority Priority)> _pending = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

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
        TrackAnalysis result = await _engine.AnalyseAsync(track.Path, ct);
        if (!ct.IsCancellationRequested) Record(track.Path, result);
        return result;
    }

    /// <summary>Queues a deck's next-up track ahead of any library work.</summary>
    public void Prefetch(Track? track)
    {
        if (track is null) return;
        lock (_gate)
        {
            int i = _pending.FindIndex(p => p.Path == track.Path);
            if (i >= 0) _pending[i] = (track.Path, AnalysisPriority.NextUp);
            else _pending.Add((track.Path, AnalysisPriority.NextUp));
        }
        Wake();
    }

    /// <summary>Replaces the pending library work with these tracks. An empty list just clears it.</summary>
    public void QueueFolder(IEnumerable<Track> tracks)
    {
        List<string> paths = tracks.Select(t => t.Path).ToList();
        lock (_gate)
        {
            _pending.RemoveAll(p => p.Priority == AnalysisPriority.Library);
            foreach (string path in paths)
                if (!_pending.Exists(p => p.Path == path))
                    _pending.Add((path, AnalysisPriority.Library));
        }
        Wake();
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
            if (_pending.Count == 0) { path = null; return false; }
            int best = 0;
            for (int i = 1; i < _pending.Count; i++)
                if (_pending[i].Priority < _pending[best].Priority) best = i;
            path = _pending[best].Path;
            _pending.RemoveAt(best);
            return true;
        }
    }

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        if (Store.Get(path)?.IsAnalysed == true) return;
        try
        {
            Record(path, await _engine.AnalyseAsync(path, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // One undecodable file must not stop the rest of the folder.
        }
    }

    private void Record(string path, TrackAnalysis result)
    {
        // Empty means it could not be decoded at all, or there is no audio context.
        // Caching that would hide a BPM a later run could find.
        if (ReferenceEquals(result, TrackAnalysis.Empty)) return;
        Store.SetAnalysis(path, result);
        _post(() => Analysed?.Invoke(this, path));
    }

    public void Dispose() => _stop.Cancel();
}
```

- [ ] **Step 4: Route deck loads through it and notice tracks the deck did not load itself**

In `DeckViewModel.cs` add `using Mkb.Mixer.App.Services;` and `using Mkb.Mixer.Library;`, add fields `private readonly AnalysisQueue _analysis;` and `private Track? _currentTrack;`, and change the constructor:

```csharp
    /// <param name="random">Injectable so tests can make shuffle deterministic.</param>
    /// <param name="analysis">Shared with the other deck and the library; a private in-memory one when omitted.</param>
    public DeckViewModel(IDeck deck, IAudioEngine engine, Random? random = null, AnalysisQueue? analysis = null)
    {
        _deck = deck;
        _engine = engine;
        _random = random ?? Random.Shared;
        _analysis = analysis ?? new AnalysisQueue(engine, TrackStore.InMemory(), manual: true);
        // SoundFlow raises this from its audio callback thread, so it has to be
        // marshalled before it touches anything bound to the UI.
        _deck.TrackEnded += (_, _) => Dispatcher.UIThread.Post(PlayNextFromPlaylist);
    }
```

Replace `LoadAsync` with:

```csharp
    /// <summary>Loads a track and kicks off its analysis in the background.</summary>
    public async Task LoadAsync(Track track)
    {
        _deck.Load(track);
        await OnTrackChangedAsync();
    }

    /// <summary>
    /// Catches up with whatever the deck now holds, whether this view model loaded
    /// it or the auto-cue did on the <see cref="IDeck"/> directly. Before this, a
    /// track brought in by the auto-cue kept the previous track's waveform.
    /// </summary>
    private async Task OnTrackChangedAsync()
    {
        Track? track = _deck.Track;
        _currentTrack = track;
        Waveform = Waveform.Empty;
        Refresh();
        if (track is null) return;

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        CancellationToken ct = _analysisCts.Token;
        try
        {
            TrackAnalysis analysis = await _analysis.AnalyseForDeckAsync(track, ct);
            if (!ct.IsCancellationRequested) Waveform = analysis.Waveform;
        }
        catch (OperationCanceledException) { /* a newer track superseded this one */ }
    }
```

Rename the existing field `_analysis` (the `CancellationTokenSource`) to `_analysisCts`. Add this as the first statement of `Refresh()`:

```csharp
        if (!ReferenceEquals(_deck.Track, _currentTrack))
        {
            _ = OnTrackChangedAsync();
            return;   // it calls Refresh again once caught up
        }
```

Keep the existing `if (_deck.Track is { } t && NowPlaying != t.Display) NowPlaying = t.Display;` line in `Refresh()`: it is now the only place `NowPlaying` is set, since the old `LoadAsync` (which set it directly) is gone.

- [ ] **Step 5: Wire the store and queue into MainViewModel**

Add `using Mkb.Mixer.App.Services;`, a field `private readonly TrackStore _tracks;`, and replace the two constructors' heads:

```csharp
    public MainViewModel() : this(new SoundFlowAudioEngine(), SettingsStore.Default(), TrackStore.Default()) { }

    /// <summary>Remembers nothing per track between runs. For tests.</summary>
    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore)
        : this(engine, settingsStore, TrackStore.InMemory()) { }

    /// <param name="post">How analysis results reach the UI thread; see <see cref="AnalysisQueue"/>.</param>
    /// <param name="manualAnalysis">No background worker; tests drive <see cref="Analysis"/> themselves.</param>
    public MainViewModel(IAudioEngine engine, SettingsStore settingsStore, TrackStore tracks,
                         Action<Action>? post = null, bool manualAnalysis = false)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        _tracks = tracks;
        Analysis = new AnalysisQueue(engine, tracks, post, manualAnalysis);

        DeckA = new DeckViewModel(engine.DeckA, engine, analysis: Analysis);
        DeckB = new DeckViewModel(engine.DeckB, engine, analysis: Analysis);
        // ... the rest of the existing constructor body, unchanged
```

Add the property `public AnalysisQueue Analysis { get; }`. At the end of `SaveState()` add `_tracks.Flush();`. In `Dispose()`, before `_engine.Dispose();`, add `Analysis.Dispose();` and `_tracks.Dispose();`.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass, including 10 new `AnalysisQueueTests`.

- [ ] **Step 7: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests/AnalysisQueueTests.cs
git commit -m "Queue background analysis, and redraw a deck whose track the auto-cue changed

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Library rows, BPM column and sorting

**Files:**
- Create: `src/Mkb.Mixer.App/ViewModels/LibraryRow.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (`BrowserTracks`, `SelectedBrowserTrack`, `_folderTracks`, `LoadFolderAsync`, `SearchAsync`, `ApplyFilter`, `Matches`, `AddToDeck`), `src/Mkb.Mixer.App/Views/MainView.axaml` (DataGrid), `src/Mkb.Mixer.App/Views/PhoneView.axaml` (tracks ListBox), `tests/Mkb.Mixer.Tests/UiSmokeTests.cs`
- Test: `tests/Mkb.Mixer.Tests/LibraryRowTests.cs`

**Interfaces:**
- Consumes: `TrackStore.Get(path).DisplayBpm`, `MainViewModel._tracks` (Task 5).
- Produces: `sealed partial class LibraryRow(Track track) : ObservableObject` with `Track Track`, observable `double? Bpm` and `bool IsPlayed`, computed `string BpmText`, `double BpmSortKey`, `string PlayedMark`, `double RowOpacity`. In `MainViewModel`: `ObservableCollection<LibraryRow> BrowserRows` (replaces `BrowserTracks`), `LibraryRow? SelectedBrowserRow` (replaces `SelectedBrowserTrack`), private `List<LibraryRow> _folderRows` (replaces `_folderTracks`), private `LibraryRow RowFor(Track t)`, private `void SetFolderRows(IEnumerable<LibraryRow> rows)`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/LibraryRowTests.cs`:

```csharp
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LibraryRowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-lib").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes empty files; tags fail to read, so titles fall back to file names.</summary>
    internal static string[] Files(string dir, params string[] names) => names.Select(n =>
    {
        string path = Path.Combine(dir, n + ".mp3");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }).ToArray();

    [Fact]
    public void UnknownBpmSortsAfterEveryKnownOne()
    {
        var rows = new[] { new LibraryRow(Track.FromPath("/a.mp3")) { Bpm = 128 },
                           new LibraryRow(Track.FromPath("/b.mp3")),
                           new LibraryRow(Track.FromPath("/c.mp3")) { Bpm = 90 } };

        Assert.Equal(["c", "a", "b"], rows.OrderBy(r => r.BpmSortKey).Select(r => r.Track.Title));
    }

    [Fact]
    public void RowsShowBpmAndPlayedState()
    {
        var row = new LibraryRow(Track.FromPath("/a.mp3"));
        Assert.Equal("—", row.BpmText);
        Assert.Equal("", row.PlayedMark);

        row.Bpm = 127.96;
        row.IsPlayed = true;

        Assert.Equal("128.0", row.BpmText);
        Assert.Equal("✓", row.PlayedMark);
        Assert.True(row.RowOpacity < 1);
    }

    [Fact]
    public async Task OpeningAFolderShowsCachedBpms()
    {
        string[] paths = Files(_dir, "a", "b");
        var store = TrackStore.InMemory();
        store.SetAnalysis(paths[0], FakeAudioEngine.Analysis(122));
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")),
            store, post: a => a(), manualAnalysis: true);

        await vm.BrowseFolderCommand.ExecuteAsync(_dir);

        Assert.Equal(2, vm.BrowserRows.Count);
        Assert.Equal(122, vm.BrowserRows.Single(r => r.Track.Path == paths[0]).Bpm);
        Assert.Null(vm.BrowserRows.Single(r => r.Track.Path == paths[1]).Bpm);
    }
}
```

In `UiSmokeTests.cs`, change every `vm.BrowserTracks.Add(x)` to `vm.BrowserRows.Add(new LibraryRow(x))` (lines ~107–109 and ~166–167). Add to the end of `WindowRendersWithoutBindingErrors` (after `window.Show();`):

```csharp
        var grid = window.GetLogicalDescendants().OfType<Avalonia.Controls.DataGrid>().Single();
        Assert.Contains(grid.Columns, c => c.SortMemberPath == "BpmSortKey");
        Assert.Contains(grid.Columns, c => c.SortMemberPath == "Track.Duration");
```

(add `using Avalonia.LogicalTree;` if missing — it is already imported).

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~LibraryRowTests"`
Expected: build error, `LibraryRow` / `BrowserRows` do not exist.

- [ ] **Step 3: Implement the row**

`src/Mkb.Mixer.App/ViewModels/LibraryRow.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>
/// One track in the library list, with the parts that change while it is on
/// screen: its BPM, which background analysis fills in, and whether it has been
/// played this session.
/// </summary>
public sealed partial class LibraryRow(Track track) : ObservableObject
{
    public Track Track { get; } = track;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText), nameof(BpmSortKey))]
    private double? _bpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedMark), nameof(RowOpacity))]
    private bool _isPlayed;

    public string BpmText => Bpm is { } b ? b.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>Unknown BPMs sort after every known one in ascending order.</summary>
    public double BpmSortKey => Bpm ?? double.MaxValue;

    public string PlayedMark => IsPlayed ? "✓" : "";

    /// <summary>Played rows are dimmed so the next pick stands out.</summary>
    public double RowOpacity => IsPlayed ? 0.5 : 1.0;
}
```

- [ ] **Step 4: Switch MainViewModel to rows**

Replace the `BrowserTracks` property and `_selectedBrowserTrack` field with:

```csharp
    /// <summary>Tracks in the selected folder, filtered by the search box.</summary>
    public ObservableCollection<LibraryRow> BrowserRows { get; } = [];

    [ObservableProperty] private LibraryRow? _selectedBrowserRow;
```

Replace `private readonly System.Collections.Generic.List<Track> _folderTracks = [];` with `private readonly System.Collections.Generic.List<LibraryRow> _folderRows = [];` and add:

```csharp
    private LibraryRow RowFor(Track t) => new(t) { Bpm = _tracks.Get(t.Path)?.DisplayBpm };

    /// <summary>Replaces what the library lists. Later tasks hook background analysis in here.</summary>
    private void SetFolderRows(System.Collections.Generic.IEnumerable<LibraryRow> rows)
    {
        _folderRows.Clear();
        _folderRows.AddRange(rows);
        ApplyFilter();
    }
```

In `LoadFolderAsync`: replace `_folderTracks.Clear(); BrowserTracks.Clear();` with `SetFolderRows([]);`, the list type with `System.Collections.Generic.List<LibraryRow>`, `list.Add(TrackMetadataReader.Read(file));` with `list.Add(RowFor(TrackMetadataReader.Read(file)));`, and `_folderTracks.AddRange(found); ApplyFilter();` with `SetFolderRows(found);`.

In `SearchAsync`: replace `BrowserTracks.Clear(); _folderTracks.Clear();` with `SetFolderRows([]);`, and the loop body's three lines with:

```csharp
                LibraryRow row = RowFor(await Task.Run(() => TrackMetadataReader.Read(file), ct));
                _folderRows.Add(row);
                BrowserRows.Add(row);
                StatusMessage = $"{BrowserRows.Count} match(es)…";
```

and the final status to `$"{BrowserRows.Count} match(es) for \"{needle}\""`.

Replace `ApplyFilter`/`Matches`:

```csharp
    private void ApplyFilter()
    {
        BrowserRows.Clear();
        foreach (LibraryRow row in _folderRows.Where(Matches))
            BrowserRows.Add(row);
    }

    private bool Matches(LibraryRow row) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        row.Track.Display.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
```

In `AddToDeck`: `if (SelectedBrowserRow is { } row) deck.Playlist.Add(row.Track);`

- [ ] **Step 5: Update the views**

`MainView.axaml` — replace the `DataGrid` element with:

```xml
            <DataGrid ItemsSource="{Binding BrowserRows}"
                      SelectedItem="{Binding SelectedBrowserRow}"
                      IsReadOnly="True" GridLinesVisibility="Horizontal"
                      HeadersVisibility="Column" CanUserSortColumns="True">
              <DataGrid.Styles>
                <Style Selector="DataGridRow" x:DataType="vm:LibraryRow">
                  <Setter Property="Opacity" Value="{Binding RowOpacity}" />
                </Style>
              </DataGrid.Styles>
              <DataGrid.Columns>
                <DataGridTextColumn Header="✓" x:DataType="vm:LibraryRow" Binding="{Binding PlayedMark}" Width="34" />
                <DataGridTextColumn Header="Title" x:DataType="vm:LibraryRow" Binding="{Binding Track.Title}" Width="*" />
                <DataGridTextColumn Header="Artist" x:DataType="vm:LibraryRow" Binding="{Binding Track.Artist}" Width="180" />
                <DataGridTextColumn Header="Album" x:DataType="vm:LibraryRow" Binding="{Binding Track.Album}" Width="160" />
                <DataGridTextColumn Header="BPM" x:DataType="vm:LibraryRow" Binding="{Binding BpmText}"
                                    SortMemberPath="BpmSortKey" Width="70" />
                <DataGridTextColumn Header="Time" x:DataType="vm:LibraryRow" Binding="{Binding Track.DurationText}"
                                    SortMemberPath="Track.Duration" Width="70" />
              </DataGrid.Columns>
            </DataGrid>
```

If the XAML compiler rejects `x:DataType` on `DataGridTextColumn`, remove those attributes (the existing columns compile without them) and keep everything else.

`PhoneView.axaml` — on the tracks `ListBox`, change the bindings to `ItemsSource="{Binding BrowserRows}" SelectedItem="{Binding SelectedBrowserRow}"` and replace its `DataTemplate` with:

```xml
                <DataTemplate x:DataType="vm:LibraryRow">
                  <Grid ColumnDefinitions="*,Auto,Auto" RowDefinitions="Auto,Auto" Opacity="{Binding RowOpacity}">
                    <TextBlock Text="{Binding Track.Title}" FontSize="14" FontWeight="SemiBold"
                               TextTrimming="CharacterEllipsis" />
                    <TextBlock Grid.Row="1" Classes="dim" Text="{Binding Track.Artist}"
                               TextTrimming="CharacterEllipsis" />
                    <TextBlock Grid.Column="1" Grid.RowSpan="2" Classes="time" Margin="10,0,0,0"
                               Text="{Binding BpmText}" VerticalAlignment="Center" />
                    <TextBlock Grid.Column="2" Grid.RowSpan="2" Classes="time" Margin="10,0,0,0"
                               Text="{Binding Track.DurationText}" VerticalAlignment="Center" />
                  </Grid>
                </DataTemplate>
```

The `xmlns:audio` import in `PhoneView.axaml` becomes unused; remove it if nothing else uses it.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests
git commit -m "Show BPM in the library and sort Time and BPM by value

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Played tracks and Recently played

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (played event), `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (session set, `RowFor`, history, roots), `src/Mkb.Mixer.App/ViewModels/FolderNode.cs` (special node)
- Test: `tests/Mkb.Mixer.Tests/PlayHistoryTests.cs`

**Interfaces:**
- Consumes: `TrackStore.MarkPlayed/RecentlyPlayed`, `LibraryRow.IsPlayed`, `SetFolderRows`, `OnTrackChangedAsync` (Tasks 4–6).
- Produces: `DeckViewModel.TrackPlayed` (`event EventHandler<Track>?`, once per load); `static TimeSpan DeckViewModel.PlayedThreshold(TimeSpan duration)`; `FolderNode.RecentlyPlayed()` and `bool FolderNode.IsRecentlyPlayed`; `Task MainViewModel.ShowRecentlyPlayedAsync()`; `const int MainViewModel.RecentlyPlayedLimit = 100`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/PlayHistoryTests.cs`:

```csharp
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class PlayHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-history").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Track T(string name, double seconds = 180) =>
        new($"/m/{name}.mp3", name, Duration: TimeSpan.FromSeconds(seconds));

    private static async Task<(DeckViewModel Deck, FakeDeck Fake, List<Track> Played)> Playing(Track track)
    {
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        var played = new List<Track>();
        deck.TrackPlayed += (_, t) => played.Add(t);
        await deck.LoadAsync(track);
        await deck.PlayCommand.ExecuteAsync(null);
        return (deck, fake, played);
    }

    [Fact]
    public async Task ATrackCountsAsPlayedAfterThirtySecondsOnce()
    {
        var (deck, fake, played) = await Playing(T("a"));

        fake.Position = TimeSpan.FromSeconds(29); deck.Refresh();
        Assert.Empty(played);

        fake.Position = TimeSpan.FromSeconds(30); deck.Refresh();
        fake.Position = TimeSpan.FromSeconds(45); deck.Refresh();
        Assert.Single(played);
    }

    [Fact]
    public async Task AShortTrackCountsAtNinetyPercent()
    {
        var (deck, fake, played) = await Playing(T("short", seconds: 20));

        fake.Position = TimeSpan.FromSeconds(18); deck.Refresh();

        Assert.Single(played);
    }

    [Fact]
    public async Task APausedDeckDoesNotCount()
    {
        var (deck, fake, played) = await Playing(T("a"));
        deck.PauseCommand.Execute(null);

        fake.Position = TimeSpan.FromSeconds(60); deck.Refresh();

        Assert.Empty(played);
    }

    [Fact]
    public async Task ATrackTheAutoCueLoadedCountsToo()
    {
        var (deck, fake, played) = await Playing(T("a"));
        fake.Position = TimeSpan.FromSeconds(31); deck.Refresh();

        fake.Load(T("b")); fake.Play(); deck.Refresh();
        fake.Position = TimeSpan.FromSeconds(31); deck.Refresh();

        Assert.Equal(["a", "b"], played.Select(t => t.Title));
    }

    [Fact]
    public async Task PlayingMarksTheRowAndTheStore()
    {
        string path = LibraryRowTests.Files(_dir, "a")[0];
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true);
        await vm.BrowseFolderCommand.ExecuteAsync(_dir);
        LibraryRow row = vm.BrowserRows.Single();

        await vm.DeckA.LoadAsync(new Track(path, "a", Duration: TimeSpan.FromMinutes(3)));
        await vm.DeckA.PlayCommand.ExecuteAsync(null);
        engine.A.Position = TimeSpan.FromSeconds(31);
        vm.Tick(TimeSpan.Zero);

        Assert.True(row.IsPlayed);
        Assert.Equal(1, store.Get(path)!.PlayCount);
    }

    [Fact]
    public async Task RecentlyPlayedIsNewestFirstAndSkipsMissingFiles()
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b", "c");
        var store = TrackStore.InMemory();
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++) store.MarkPlayed(paths[i], t0.AddHours(i));
        File.Delete(paths[2]);
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true);

        await vm.ShowRecentlyPlayedAsync();

        Assert.Equal(["b", "a"], vm.BrowserRows.Select(r => r.Track.Title));
    }

    [Fact]
    public void RecentlyPlayedIsTheFirstRoot()
    {
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")));

        vm.LoadRoots();

        Assert.True(vm.Roots[0].IsRecentlyPlayed);
        Assert.Empty(vm.Roots[0].Children);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~PlayHistoryTests"`
Expected: build error, `TrackPlayed` / `ShowRecentlyPlayedAsync` / `IsRecentlyPlayed` do not exist.

- [ ] **Step 3: Report plays from the deck**

In `DeckViewModel.cs` add:

```csharp
    /// <summary>Raised once per loaded track, when it has played long enough to count, or ended.</summary>
    public event EventHandler<Track>? TrackPlayed;

    private bool _playedReported;

    /// <summary>30 s, or 90% of a track shorter than that.</summary>
    public static TimeSpan PlayedThreshold(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration * 0.9 < TimeSpan.FromSeconds(30)
            ? duration * 0.9
            : TimeSpan.FromSeconds(30);

    private void ReportPlayed()
    {
        if (_playedReported || _deck.Track is not { } t) return;
        _playedReported = true;
        TrackPlayed?.Invoke(this, t);
    }
```

In `OnTrackChangedAsync`, after `_currentTrack = track;` add `_playedReported = false;`. At the end of `Refresh()` add:

```csharp
        if (IsPlaying && pos >= PlayedThreshold(dur)) ReportPlayed();
```

Change the `TrackEnded` subscription in the constructor to report the finished track before moving on:

```csharp
        _deck.TrackEnded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ReportPlayed();
            PlayNextFromPlaylist();
        });
```

- [ ] **Step 4: The Recently played node**

In `FolderNode.cs` add a property and factory, and guard the existing path-only constructor chain:

```csharp
    /// <summary>The fixed "Recently played" entry at the top of the tree. It has no path or children.</summary>
    public static FolderNode RecentlyPlayed() => new();

    private FolderNode()
    {
        Path = string.Empty;
        Name = "★ Recently played";
        IsRecentlyPlayed = true;
    }

    public bool IsRecentlyPlayed { get; }
```

- [ ] **Step 5: Session marks, history and the node in MainViewModel**

Add:

```csharp
    public const int RecentlyPlayedLimit = 100;

    /// <summary>Paths played since the app started, for the library's ✓.</summary>
    private readonly System.Collections.Generic.HashSet<string> _sessionPlayed = [];
```

Change `RowFor` to:

```csharp
    private LibraryRow RowFor(Track t)
    {
        bool played;
        lock (_sessionPlayed) played = _sessionPlayed.Contains(t.Path);
        return new LibraryRow(t) { Bpm = _tracks.Get(t.Path)?.DisplayBpm, IsPlayed = played };
    }
```

In the constructor, after the decks are created:

```csharp
        DeckA.TrackPlayed += (_, t) => OnTrackPlayed(t);
        DeckB.TrackPlayed += (_, t) => OnTrackPlayed(t);
```

and add:

```csharp
    private void OnTrackPlayed(Track track)
    {
        _tracks.MarkPlayed(track.Path, DateTime.UtcNow);
        lock (_sessionPlayed) _sessionPlayed.Add(track.Path);
        foreach (LibraryRow row in _folderRows.Where(r => r.Track.Path == track.Path))
            row.IsPlayed = true;
    }

    /// <summary>Lists the most recently played tracks that still exist, newest first.</summary>
    public async Task ShowRecentlyPlayedAsync()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        CancellationToken ct = _scan.Token;
        try
        {
            var rows = await Task.Run(() => _tracks.RecentlyPlayed(RecentlyPlayedLimit)
                .Where(File.Exists)
                .Select(path => { ct.ThrowIfCancellationRequested(); return RowFor(TrackMetadataReader.Read(path)); })
                .ToList(), ct);
            SetFolderRows(rows);
            StatusMessage = $"{rows.Count} recently played track(s)";
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
    }
```

In `LoadRoots()`, make `Roots.Add(FolderNode.RecentlyPlayed());` the first line after `Roots.Clear();`. Change `OnSelectedFolderChanged`:

```csharp
    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        if (value is { IsRecentlyPlayed: true })
            _ = ShowRecentlyPlayedAsync();
        // The "…" placeholder stands in for unexpanded children and is not a real
        // path; browsing it would fail with "could not find a part of the path".
        else if (value is { IsPlaceholder: false })
            _ = LoadFolderAsync(value.Path);
    }
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests/PlayHistoryTests.cs
git commit -m "Tick played tracks in the library and list recently played ones

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Deck BPM, x1/2 and x2, SYNC and nudge

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`, `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (link decks, refresh on analysis)
- Test: `tests/Mkb.Mixer.Tests/DeckBpmTests.cs`

**Interfaces:**
- Consumes: `AnalysisQueue.Store/Analysed`, `TrackInfo.DisplayBpm/BpmMultiplier`, `OnTrackChangedAsync` (Tasks 4–5).
- Produces on `DeckViewModel`: observable `double? TrackBpm`; `double? HeardBpm` (= `TrackBpm * Tempo`); `string BpmText` ("128.0 BPM" / "— BPM"); `DeckViewModel? Other` (set by `MainViewModel`); `bool HasTrack`; `IRelayCommand SyncCommand` with `string SyncHint`; `IRelayCommand HalveBpmCommand`, `DoubleBpmCommand`; `void BeginNudge(int direction)`, `void EndNudge()`; `const double NudgeAmount = 0.04`; private `void ApplyStoredInfo()` that Task 10 extends.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/DeckBpmTests.cs`:

```csharp
using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class DeckBpmTests
{
    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(4));

    private static (DeckViewModel A, DeckViewModel B, FakeAudioEngine Engine, TrackStore Store) Pair()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true);
        var a = new DeckViewModel(engine.A, engine, analysis: queue);
        var b = new DeckViewModel(engine.B, engine, analysis: queue);
        a.Other = b;
        b.Other = a;
        return (a, b, engine, store);
    }

    [Fact]
    public async Task ShowsTheBpmAsHeard()
    {
        var (a, _, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));

        a.Tempo = 1.1;

        Assert.Equal(120, a.TrackBpm);
        Assert.Equal(132, a.HeardBpm!.Value, 3);
        Assert.Equal("132.0 BPM", a.BpmText);
    }

    [Fact]
    public async Task AnUnknownBpmShowsADash()
    {
        var (a, _, _, _) = Pair();
        await a.LoadAsync(T("x"));

        Assert.Null(a.TrackBpm);
        Assert.Equal("— BPM", a.BpmText);
    }

    [Fact]
    public async Task SyncMatchesTheOtherDeckAsHeard()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        engine.Analyses["/m/y.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));
        await b.LoadAsync(T("y"));
        b.Tempo = 1.05;

        a.SyncCommand.Execute(null);

        Assert.Equal(1.05, a.Tempo, 4);
        Assert.Equal(126, a.HeardBpm!.Value, 3);
    }

    [Fact]
    public async Task SyncStaysWithinTheSliderRange()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(100);
        engine.Analyses["/m/y.mp3"] = FakeAudioEngine.Analysis(170);
        await a.LoadAsync(T("x"));
        await b.LoadAsync(T("y"));

        a.SyncCommand.Execute(null);

        Assert.Equal(1.5, a.Tempo, 4);
    }

    [Fact]
    public async Task SyncIsDisabledUntilBothBpmsAreKnown()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));
        a.Refresh();
        Assert.False(a.SyncCommand.CanExecute(null));
        Assert.Equal("Nothing is loaded on the other deck", a.SyncHint);

        await b.LoadAsync(T("y"));   // no BPM found
        a.Refresh();
        Assert.False(a.SyncCommand.CanExecute(null));
        Assert.Equal("The other deck's BPM isn't known yet", a.SyncHint);
    }

    [Fact]
    public async Task NudgeReturnsExactlyToTheTempoItStartedFrom()
    {
        var (a, _, _, _) = Pair();
        await a.LoadAsync(T("x"));
        a.Tempo = 1.07;

        a.BeginNudge(+1);
        Assert.Equal(1.07 * 1.04, a.Tempo, 6);
        a.BeginNudge(+1);   // a second press while held changes nothing
        a.EndNudge();

        Assert.Equal(1.07, a.Tempo);
    }

    [Fact]
    public async Task HalvingTheBpmIsRememberedForTheTrack()
    {
        var (a, _, engine, store) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(140);
        await a.LoadAsync(T("x"));

        a.HalveBpmCommand.Execute(null);

        Assert.Equal(70, a.TrackBpm);
        Assert.Equal(0.5, store.Get("/m/x.mp3")!.BpmMultiplier);

        var again = new DeckViewModel(new FakeDeck(DeckId.B), engine,
            analysis: new AnalysisQueue(engine, store, post: x => x(), manual: true));
        await again.LoadAsync(T("x"));
        Assert.Equal(70, again.TrackBpm);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~DeckBpmTests"`
Expected: build error, `TrackBpm` and friends do not exist.

- [ ] **Step 3: Implement on the deck**

In `DeckViewModel.cs` add `using System.Globalization;` and:

```csharp
    public const double NudgeAmount = 0.04;
    private double? _nudgeFrom;
    private string _syncHint = string.Empty;

    /// <summary>The track's BPM at normal speed, after any x1/2 or x2 correction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeardBpm), nameof(BpmText))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private double? _trackBpm;

    /// <summary>The BPM coming out of the speakers: the track's BPM times the tempo.</summary>
    public double? HeardBpm => TrackBpm * Tempo;

    public string BpmText => HeardBpm is { } b ? $"{b.ToString("0.0", CultureInfo.InvariantCulture)} BPM" : "— BPM";

    /// <summary>The other deck, for SYNC. Set once by <see cref="MainViewModel"/>.</summary>
    public DeckViewModel? Other { get; set; }

    public bool HasTrack => _deck.Track is not null;

    /// <summary>Why SYNC is unavailable, or what it does when it is.</summary>
    public string SyncHint => SyncBlocker ?? "Match this deck's tempo to the other deck";

    private string? SyncBlocker =>
        TrackBpm is null ? "This deck's BPM isn't known yet"
        : Other is not { HasTrack: true } ? "Nothing is loaded on the other deck"
        : Other.HeardBpm is null ? "The other deck's BPM isn't known yet"
        : null;

    private bool CanSync() => SyncBlocker is null;

    /// <summary>One-shot: sets this deck's tempo so it plays at the other deck's BPM.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private void Sync()
    {
        if (TrackBpm is not { } mine || Other?.HeardBpm is not { } theirs) return;
        Tempo = Math.Clamp(theirs / mine, 0.5, 1.5);
    }

    [RelayCommand]
    private void HalveBpm() => ScaleBpm(0.5);

    [RelayCommand]
    private void DoubleBpm() => ScaleBpm(2);

    private void ScaleBpm(double factor)
    {
        if (_deck.Track is not { } t || TrackBpm is null) return;
        _analysis.Store.Update(t.Path, i => i with { BpmMultiplier = Math.Clamp(i.BpmMultiplier * factor, 0.25, 4) });
        ApplyStoredInfo();
    }

    /// <summary>Holding a nudge button briefly speeds up or slows down, to line beats up by ear.</summary>
    public void BeginNudge(int direction)
    {
        if (_nudgeFrom is not null) return;
        _nudgeFrom = Tempo;
        Tempo = Math.Clamp(Tempo * (1 + NudgeAmount * Math.Sign(direction)), 0.5, 1.5);
    }

    public void EndNudge()
    {
        if (_nudgeFrom is not { } from) return;
        _nudgeFrom = null;
        Tempo = from;
    }

    /// <summary>Pulls what the store knows about the loaded track onto the deck.</summary>
    private void ApplyStoredInfo()
    {
        TrackInfo? info = _deck.Track is { } t ? _analysis.Store.Get(t.Path) : null;
        TrackBpm = info?.DisplayBpm;
    }
```

Change `OnTempoChanged` so the readout follows the slider:

```csharp
    partial void OnTempoChanged(double value)
    {
        _deck.Tempo = (float)value;
        OnPropertyChanged(nameof(TempoLabel));
        OnPropertyChanged(nameof(HeardBpm));
        OnPropertyChanged(nameof(BpmText));
    }
```

In `OnTrackChangedAsync`, call `ApplyStoredInfo();` right after `Waveform = Waveform.Empty;` (a cached BPM shows at once), and inside the `if (!ct.IsCancellationRequested)` block after setting `Waveform`, call `ApplyStoredInfo();` again.

In the constructor, add:

```csharp
        _analysis.Analysed += (_, path) =>
        {
            if (_deck.Track?.Path == path) ApplyStoredInfo();
        };
```

At the end of `Refresh()` add (SYNC depends on the other deck, which can change at any time):

```csharp
        SyncCommand.NotifyCanExecuteChanged();
        string hint = SyncHint;
        if (hint != _syncHint) { _syncHint = hint; OnPropertyChanged(nameof(SyncHint)); }
```

- [ ] **Step 4: Link the decks**

In `MainViewModel`'s constructor, after creating both decks: `DeckA.Other = DeckB; DeckB.Other = DeckA;`

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests/DeckBpmTests.cs
git commit -m "Show each deck's BPM, sync one deck to the other, and nudge by ear

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Library BPM filter, ≈, and background analysis setting

**Files:**
- Modify: `src/Mkb.Mixer.Library/Settings.cs`, `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs`, `src/Mkb.Mixer.App/Views/MainView.axaml` (library header), `src/Mkb.Mixer.App/Views/PhoneView.axaml` (library tab)
- Test: `tests/Mkb.Mixer.Tests/LibraryFilterTests.cs`

**Interfaces:**
- Consumes: `LibraryRow`, `SetFolderRows`, `RowFor`, `_folderRows` (Task 6); `DeckViewModel.HeardBpm`, `IsPlaying` (Task 8); `AnalysisQueue.QueueFolder/Analysed` (Task 5).
- Produces: `AppSettings.AnalyseLibraryBpm` (`bool?`); on `MainViewModel`: observable `decimal? BpmMin`, `decimal? BpmMax`, `bool AnalyseLibraryBpm`; `IRelayCommand MatchBpmCommand`; `const double MatchBpmRange = 0.06`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/LibraryFilterTests.cs`:

```csharp
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
    public void TheSettingDefaultsOnAwayFromPhones()
    {
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "fresh.json")));

        Assert.True(vm.AnalyseLibraryBpm);   // tests run with App.UsePhoneLayout false
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~LibraryFilterTests"`
Expected: build error, `BpmMin` and friends do not exist.

- [ ] **Step 3: Implement**

`Settings.cs` — add to `AppSettings`:

```csharp
    /// <summary>Analyse library folders for BPM in the background. Null: on, except on phones.</summary>
    public bool? AnalyseLibraryBpm { get; set; }
```

`MainViewModel.cs`:

```csharp
    /// <summary>The ≈ button's window around the audible deck's BPM.</summary>
    public const double MatchBpmRange = 0.06;

    [ObservableProperty] private decimal? _bpmMin;
    [ObservableProperty] private decimal? _bpmMax;

    /// <summary>Analyse every track in an opened folder, lowest priority. Costs battery on phones.</summary>
    [ObservableProperty] private bool _analyseLibraryBpm;

    partial void OnBpmMinChanged(decimal? value) => ApplyFilter();
    partial void OnBpmMaxChanged(decimal? value) => ApplyFilter();
    partial void OnAnalyseLibraryBpmChanged(bool value) => QueueLibraryAnalysis();

    private void QueueLibraryAnalysis() =>
        Analysis.QueueFolder(AnalyseLibraryBpm ? _folderRows.Select(r => r.Track) : []);

    private bool InBpmRange(double? bpm)
    {
        if (BpmMin is null && BpmMax is null) return true;
        if (bpm is null) return false;
        double lo = BpmMin is { } min ? (double)min : 0;
        double hi = BpmMax is { } max ? (double)max : double.MaxValue;
        if (lo > hi) (lo, hi) = (hi, lo);
        return bpm >= lo && bpm <= hi;
    }

    /// <summary>The deck the room is hearing: the playing one in front on the crossfader.</summary>
    private DeckViewModel AudibleDeck()
    {
        DeckViewModel front = CrossfaderPosition <= Crossfader.Centre ? DeckA : DeckB;
        DeckViewModel back = ReferenceEquals(front, DeckA) ? DeckB : DeckA;
        return !front.IsPlaying && back.IsPlaying ? back : front;
    }

    /// <summary>Filters the library to what will mix with the audible deck.</summary>
    [RelayCommand]
    private void MatchBpm()
    {
        if (AudibleDeck().HeardBpm is not { } bpm)
        {
            StatusMessage = "No BPM to match: the playing deck's BPM isn't known yet";
            return;
        }
        BpmMin = Math.Round((decimal)(bpm * (1 - MatchBpmRange)), 1);
        BpmMax = Math.Round((decimal)(bpm * (1 + MatchBpmRange)), 1);
    }

    private void OnAnalysed(string path)
    {
        double? bpm = _tracks.Get(path)?.DisplayBpm;
        foreach (LibraryRow row in _folderRows.Where(r => r.Track.Path == path))
        {
            row.Bpm = bpm;
            // Add a newly matching row without rebuilding the list, so the
            // selection survives a background folder scan.
            if (Matches(row) && !BrowserRows.Contains(row)) BrowserRows.Add(row);
        }
    }
```

Change `Matches`:

```csharp
    private bool Matches(LibraryRow row) =>
        InBpmRange(row.Bpm) &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         row.Track.Display.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
```

In `SetFolderRows`, after `ApplyFilter();` add `QueueLibraryAnalysis();`. In `SearchAsync`, after the loop finishes (before the final `StatusMessage`), add `QueueLibraryAnalysis();`.

In the constructor, next to the other `_settings` reads: `_analyseLibraryBpm = _settings.AnalyseLibraryBpm ?? !App.UsePhoneLayout;` and subscribe `Analysis.Analysed += (_, path) => OnAnalysed(path);`. In `SaveState()`: `_settings.AnalyseLibraryBpm = AnalyseLibraryBpm;`.

- [ ] **Step 4: Views**

`MainView.axaml` — the library header grid becomes `ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"`; keep LIBRARY, search box, Search and Stop in columns 0–3, move "Save playlists" to column 6 and "Load playlists" to column 7, and insert:

```xml
          <StackPanel Grid.Column="4" Orientation="Horizontal" Spacing="4" Margin="16,0,0,0"
                      VerticalAlignment="Center">
            <TextBlock Classes="dim" Text="BPM" VerticalAlignment="Center" />
            <NumericUpDown Classes="bpmfilter" Value="{Binding BpmMin}" Watermark="from"
                           ToolTip.Tip="Lowest BPM to list. Leave both empty to list everything." />
            <TextBlock Classes="dim" Text="–" VerticalAlignment="Center" />
            <NumericUpDown Classes="bpmfilter" Value="{Binding BpmMax}" Watermark="to"
                           ToolTip.Tip="Highest BPM to list" />
            <Button Classes="tiny" Content="≈" Command="{Binding MatchBpmCommand}"
                    ToolTip.Tip="List tracks within 6% of the playing deck's BPM" />
          </StackPanel>
          <CheckBox Grid.Column="5" Content="analyse BPM" Margin="12,0,0,0"
                    IsChecked="{Binding AnalyseLibraryBpm}"
                    ToolTip.Tip="Work out the BPM of every track in an opened folder, in the background" />
```

`Styles.axaml` — add:

```xml
  <Style Selector="NumericUpDown.bpmfilter">
    <Setter Property="Width" Value="64" />
    <Setter Property="ShowButtonSpinner" Value="False" />
    <Setter Property="Minimum" Value="40" />
    <Setter Property="Maximum" Value="250" />
    <Setter Property="FormatString" Value="0.#" />
  </Style>
```

`PhoneView.axaml` — the library tab grid becomes `RowDefinitions="Auto,Auto,2*,Auto,3*,Auto"`; add after the search row, and bump the `Grid.Row` of every later child by one (tree 1→2, progress 2→3, tracks 3→4, buttons 4→5):

```xml
          <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="6" Margin="0,8,0,0">
            <TextBlock Classes="dim" Text="BPM" VerticalAlignment="Center" />
            <NumericUpDown Classes="bpmfilter" Value="{Binding BpmMin}" Watermark="from" />
            <TextBlock Classes="dim" Text="–" VerticalAlignment="Center" />
            <NumericUpDown Classes="bpmfilter" Value="{Binding BpmMax}" Watermark="to" />
            <Button Classes="tiny" Content="≈" Command="{Binding MatchBpmCommand}" />
            <CheckBox Content="analyse" IsChecked="{Binding AnalyseLibraryBpm}" Margin="6,0,0,0" />
          </StackPanel>
```

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src tests/Mkb.Mixer.Tests/LibraryFilterTests.cs
git commit -m "Filter the library by BPM, match the playing deck, and analyse folders in the background

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 10: Hot cues on the deck

**Files:**
- Create: `src/Mkb.Mixer.App/ViewModels/HotCueSlot.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (`HotCues`, commands, `ApplyStoredInfo`, `Refresh`, `PlayAsync`), `tests/Mkb.Mixer.Tests/FakeDeck.cs`
- Test: `tests/Mkb.Mixer.Tests/HotCueTests.cs`

**Interfaces:**
- Consumes: `TrackStore.Update`, `TrackInfo.WithHotCue/HotCueSeconds/HotCueCount`, `ApplyStoredInfo` (Tasks 4, 8).
- Produces: `sealed partial class HotCueSlot(int index) : ObservableObject` with `int Index`, `string Label` ("1".."4"), observable `double? Seconds`, `bool IsSet`. On `DeckViewModel`: `IReadOnlyList<HotCueSlot> HotCues`, `IRelayCommand<HotCueSlot> HotCueCommand` (set or jump), `IRelayCommand<HotCueSlot> ClearHotCueCommand`, `double?[] CueFractions` (0..1 per slot, raises `PropertyChanged`). `FakeDeck.RewindsOnPlayFromStop`.

- [ ] **Step 1: Write the failing tests**

In `FakeDeck.cs` add the property and use it in `Play`:

```csharp
    /// <summary>Mimics a backend that restarts a stopped player from the top when it plays.</summary>
    public bool RewindsOnPlayFromStop { get; set; }

    public void Play()
    {
        if (RewindsOnPlayFromStop && State == PlaybackState.Stopped) Position = TimeSpan.Zero;
        State = PlaybackState.Playing;
        PlayCount++;
    }
```

(replacing the existing one-line `Play`).

`tests/Mkb.Mixer.Tests/HotCueTests.cs`:

```csharp
using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class HotCueTests
{
    private static readonly Track Song = new("/m/song.mp3", "song", Duration: TimeSpan.FromMinutes(4));

    private static DeckViewModel Deck(FakeAudioEngine engine, TrackStore store) =>
        new(engine.A, engine, analysis: new AnalysisQueue(engine, store, post: a => a(), manual: true));

    private static async Task<(DeckViewModel Deck, FakeDeck Fake, TrackStore Store)> Loaded()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        DeckViewModel deck = Deck(engine, store);
        await deck.LoadAsync(Song);
        return (deck, engine.A, store);
    }

    [Fact]
    public async Task TappingAnEmptySlotSetsItAtThePlayhead()
    {
        var (deck, fake, store) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(42);

        deck.HotCueCommand.Execute(deck.HotCues[1]);

        Assert.Equal(42, deck.HotCues[1].Seconds);
        Assert.Equal(42, store.Get(Song.Path)!.HotCueSeconds[1]);
        Assert.Equal(42.0 / 240, deck.CueFractions[1]!.Value, 6);
    }

    [Fact]
    public async Task TappingASetSlotJumpsThereAndKeepsPlaying()
    {
        var (deck, fake, _) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[0]);
        await deck.PlayCommand.ExecuteAsync(null);
        fake.Position = TimeSpan.FromSeconds(100);

        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.Equal(TimeSpan.FromSeconds(10), fake.Position);
        Assert.Equal(PlaybackState.Playing, fake.State);
    }

    [Fact]
    public async Task WhileStoppedItWaitsAtTheCueAndPlaysFromThere()
    {
        var (deck, fake, _) = await Loaded();
        fake.RewindsOnPlayFromStop = true;
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[0]);
        fake.Position = TimeSpan.Zero;

        deck.HotCueCommand.Execute(deck.HotCues[0]);
        Assert.Equal(PlaybackState.Stopped, fake.State);
        await deck.PlayCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromSeconds(10), fake.Position);
    }

    [Fact]
    public async Task ClearingEmptiesTheSlotForGood()
    {
        var (deck, fake, store) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[2]);

        deck.ClearHotCueCommand.Execute(deck.HotCues[2]);

        Assert.False(deck.HotCues[2].IsSet);
        Assert.Null(store.Get(Song.Path)!.HotCueSeconds[2]);
        Assert.Null(deck.CueFractions[2]);
    }

    [Fact]
    public async Task ACueAtTheVeryStartCountsAsSet()
    {
        var (deck, fake, _) = await Loaded();
        deck.HotCueCommand.Execute(deck.HotCues[0]);   // playhead at 0:00
        Assert.True(deck.HotCues[0].IsSet);

        fake.Position = TimeSpan.FromSeconds(50);
        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.Equal(TimeSpan.Zero, fake.Position);
        Assert.Equal(0, deck.HotCues[0].Seconds);
    }

    [Fact]
    public async Task CuesComeBackWithTheTrack()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        store.Update(Song.Path, i => i.WithHotCue(3, 77));

        DeckViewModel deck = Deck(engine, store);
        await deck.LoadAsync(Song);

        Assert.Equal(77, deck.HotCues[3].Seconds);
    }

    [Fact]
    public void WithNothingLoadedTappingDoesNothing()
    {
        var engine = new FakeAudioEngine();
        DeckViewModel deck = Deck(engine, TrackStore.InMemory());

        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.False(deck.HotCues[0].IsSet);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~HotCueTests"`
Expected: build error, `HotCues` does not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.App/ViewModels/HotCueSlot.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>One of a deck's numbered hot cue buttons.</summary>
public sealed partial class HotCueSlot(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Where the cue is, or null when the slot is empty. 0 is a real cue at the start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet))]
    private double? _seconds;

    public bool IsSet => Seconds is not null;
}
```

In `DeckViewModel.cs` add `using System.Collections.Generic;` (the App project has no implicit usings) and:

```csharp
    public IReadOnlyList<HotCueSlot> HotCues { get; } =
        Enumerable.Range(0, TrackInfo.HotCueCount).Select(i => new HotCueSlot(i)).ToArray();

    /// <summary>Each slot's position as 0..1 of the track, for the waveform markers.</summary>
    public double?[] CueFractions { get; private set; } = new double?[TrackInfo.HotCueCount];

    private TimeSpan _cueFractionsDuration;

    /// <summary>Sets an empty slot at the playhead, or jumps to a set one keeping play/stop as it is.</summary>
    [RelayCommand]
    private void HotCue(HotCueSlot slot)
    {
        if (_deck.Track is not { } t) return;
        if (slot.Seconds is { } seconds)
        {
            _deck.Seek(TimeSpan.FromSeconds(seconds));
            Refresh();
            return;
        }
        slot.Seconds = _deck.Position.TotalSeconds;
        SaveHotCue(t, slot);
    }

    [RelayCommand]
    private void ClearHotCue(HotCueSlot slot)
    {
        if (_deck.Track is not { } t || !slot.IsSet) return;
        slot.Seconds = null;
        SaveHotCue(t, slot);
    }

    private void SaveHotCue(Track track, HotCueSlot slot)
    {
        _analysis.Store.Update(track.Path, i => i.WithHotCue(slot.Index, slot.Seconds));
        UpdateCueFractions();
    }

    private void UpdateCueFractions()
    {
        TimeSpan duration = _deck.Duration;
        _cueFractionsDuration = duration;
        CueFractions = HotCues
            .Select(c => c.Seconds is { } s && duration > TimeSpan.Zero
                ? Math.Clamp(s / duration.TotalSeconds, 0, 1)
                : (double?)null)
            .ToArray();
        OnPropertyChanged(nameof(CueFractions));
    }
```

Extend `ApplyStoredInfo` (after `TrackBpm = ...`):

```csharp
        foreach (HotCueSlot slot in HotCues)
            slot.Seconds = info?.HotCueSeconds[slot.Index];
        UpdateCueFractions();
```

In `Refresh()`, after `dur` is read, add `if (dur != _cueFractionsDuration) UpdateCueFractions();` (the duration is often unknown until the backend has opened the file).

In `PlayAsync`, replace `_deck.Play();` with:

```csharp
        bool fromStop = _deck.State == PlaybackState.Stopped;
        TimeSpan at = _deck.Position;
        _deck.Play();
        // Some backends restart a stopped player from the top; keep a hot cue
        // jump made while stopped.
        if (fromStop && at > TimeSpan.Zero) _deck.Seek(at);
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests/FakeDeck.cs tests/Mkb.Mixer.Tests/HotCueTests.cs
git commit -m "Add four hot cues per deck, saved with each track

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Deck controls row, waveform markers and the tablet render

**Files:**
- Create: `src/Mkb.Mixer.App/Controls/HotCuePalette.cs`, `src/Mkb.Mixer.App/Converters/HotCueBrushConverter.cs`
- Modify: `src/Mkb.Mixer.App/Controls/WaveformView.cs`, `src/Mkb.Mixer.App/Views/DeckView.axaml`, `src/Mkb.Mixer.App/Views/DeckView.axaml.cs`, `src/Mkb.Mixer.App/Styles.axaml`, `tests/Mkb.Mixer.Tests/UiSmokeTests.cs`
- Test: `tests/Mkb.Mixer.Tests/UiSmokeTests.cs`

**Interfaces:**
- Consumes: everything on `DeckViewModel` from Tasks 8 and 10.
- Produces: `static class HotCuePalette` with `static IBrush Brush(int index)`; `HotCueBrushConverter : IValueConverter` (int → `IBrush`), resource key `HotCueBrush` in `Styles.axaml`; `WaveformView.CueFractions` (`double?[]?` styled property); named buttons `NudgeDown`/`NudgeUp` in `DeckView`.

- [ ] **Step 1: Write the failing UI tests**

In `UiSmokeTests.cs`, extract the deck/library setup from `RendersPopulatedLayoutToPng` (everything from `// Give both decks...` through `vm.DeckA.Shuffle = true;`) into a helper and call it from that test:

```csharp
    /// <summary>Two loaded decks with BPMs, hot cues and playlists, and a few library rows.</summary>
    private static void Populate(MainViewModel vm, FakeAudioEngine engine)
    {
        var a = new Track("/music/sandstorm.mp3", "Sandstorm", "Darude", "Before the Storm", TimeSpan.FromSeconds(224));
        var b = new Track("/music/kalimba.mp3", "Kalimba", "Mr. Scruff", "Ninja Tuna", TimeSpan.FromSeconds(348));
        engine.Analyses[a.Path] = FakeAudioEngine.Analysis(136);
        engine.Analyses[b.Path] = FakeAudioEngine.Analysis(122);

        vm.DeckA.LoadAsync(a).Wait();
        vm.DeckB.LoadAsync(b).Wait();
        engine.A.Position = TimeSpan.FromSeconds(30);
        vm.DeckA.HotCueCommand.Execute(vm.DeckA.HotCues[0]);
        engine.A.Position = TimeSpan.FromSeconds(150);
        vm.DeckA.HotCueCommand.Execute(vm.DeckA.HotCues[2]);
        engine.A.Position = TimeSpan.FromSeconds(96);
        engine.A.Play();
        engine.B.Position = TimeSpan.FromSeconds(18);
        vm.DeckA.Refresh();
        vm.DeckB.Refresh();
        vm.DeckB.IsMuted = true;

        vm.DeckA.Playlist.Add(new Track("/m/1.mp3", "Sandstorm", "Darude", "Before the Storm", TimeSpan.FromSeconds(224)));
        vm.DeckA.Playlist.Add(new Track("/m/2.mp3", "Get Up", "Technotronic", "Pump Up the Jam", TimeSpan.FromSeconds(203)));
        vm.DeckB.Playlist.Add(new Track("/m/3.mp3", "Kalimba", "Mr. Scruff", "Ninja Tuna", TimeSpan.FromSeconds(348)));

        vm.BrowserRows.Add(new LibraryRow(a) { Bpm = 136, IsPlayed = true });
        vm.BrowserRows.Add(new LibraryRow(b) { Bpm = 122 });
        vm.BrowserRows.Add(new LibraryRow(new Track("/m/4.mp3", "Windowlicker", "Aphex Twin", "Windowlicker", TimeSpan.FromSeconds(366))));
        vm.CrossfaderPosition = 0.35f;
        vm.AutoCueEnabled = true;
        vm.StatusMessage = "3 track(s) in Demo";
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.DeckB.IsCued = true;
        vm.DeckA.Shuffle = true;
    }
```

Use `Populate(vm, engine);` in the phone render test too, in place of its own deck/library setup lines (keep its `vm.CueMode = CueMode.Split; vm.DeckA.IsCued = true;`).

Add:

```csharp
    [Fact]
    public void DecksShowFourHotCuesEachAndBpmControls() => AvaloniaTest.Run(() =>
    {
        var (window, vm, engine) = Build();
        Populate(vm, engine);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        List<Avalonia.Controls.Button> buttons = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().ToList();
        Assert.Equal(8, buttons.Count(b => b.Classes.Contains("hotcue")));
        Assert.Equal(2, buttons.Count(b => b.Content as string == "SYNC"));
        Assert.Contains(buttons, b => b.Content as string == "136.0 BPM");
    });

    /// <summary>The Android tablet layout: the desktop view with the "touch" class.</summary>
    [Fact]
    public void RendersTabletLayoutToPng() => AvaloniaTest.Run(() =>
    {
        var (_, vm, engine) = Build();
        Populate(vm, engine);
        var view = new MainView { DataContext = vm };
        view.Classes.Add("touch");
        var window = new Avalonia.Controls.Window { Content = view, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        string dir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("MKB_UI_SNAPSHOT")
                                           ?? Path.Combine(Path.GetTempPath(), "x.png"))!;
        string output = Path.Combine(dir, "mkb-mixer-tablet.png");
        using Bitmap frame = window.CaptureRenderedFrame()
                             ?? throw new InvalidOperationException("no frame captured");
#pragma warning disable CS0618
        frame.Save(output);
#pragma warning restore CS0618
        Assert.True(new FileInfo(output).Length > 10_000, "rendered frame looks empty");
    });
```

(add `using Avalonia.VisualTree;` and `using Mkb.Mixer.App.ViewModels;` if not already imported).

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~UiSmokeTests"`
Expected: `DecksShowFourHotCuesEachAndBpmControls` fails (no hot cue buttons).

- [ ] **Step 3: Palette, converter and waveform markers**

`src/Mkb.Mixer.App/Controls/HotCuePalette.cs`:

```csharp
using Avalonia.Media;

namespace Mkb.Mixer.App.Controls;

/// <summary>The hot cue colours, shared by the deck buttons and the waveform markers so they always agree.</summary>
public static class HotCuePalette
{
    // Not gold: the waveform already uses the accent for the played part.
    private static readonly IBrush[] Brushes =
    [
        new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
        new SolidColorBrush(Color.FromRgb(0x3C, 0xD0, 0x70)),
        new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xF0)),
        new SolidColorBrush(Color.FromRgb(0xC0, 0x7C, 0xF0)),
    ];

    public static IBrush Brush(int index) => Brushes[(index % Brushes.Length + Brushes.Length) % Brushes.Length];
}
```

`src/Mkb.Mixer.App/Converters/HotCueBrushConverter.cs`:

```csharp
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Mkb.Mixer.App.Controls;

namespace Mkb.Mixer.App.Converters;

/// <summary>A hot cue slot's index to its colour.</summary>
public sealed class HotCueBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? HotCuePalette.Brush(index) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

`WaveformView.cs` — add `using System.Globalization;`, the property, include it in `AffectsRender`, and draw the markers at the end of `Render` (after the playhead line):

```csharp
    /// <summary>Hot cue positions as 0..1 of the track, one per slot, null where a slot is empty.</summary>
    public static readonly StyledProperty<double?[]?> CueFractionsProperty =
        AvaloniaProperty.Register<WaveformView, double?[]?>(nameof(CueFractions));

    public double?[]? CueFractions
    {
        get => GetValue(CueFractionsProperty);
        set => SetValue(CueFractionsProperty, value);
    }
```

```csharp
        if (CueFractions is not { } cues) return;
        for (int i = 0; i < cues.Length; i++)
        {
            if (cues[i] is not { } fraction) continue;
            IBrush brush = HotCuePalette.Brush(i);
            double x = Math.Round(bounds.Width * fraction) + 0.5;
            context.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, bounds.Height));
            var label = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.Black);
            context.FillRectangle(brush, new Rect(x, 0, label.Width + 4, label.Height));
            context.DrawText(label, new Point(x + 2, 0));
        }
```

- [ ] **Step 4: The deck row**

`DeckView.axaml`: the outer grid becomes `RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto,*"`; the `PlaylistView` moves to `Grid.Row="6"`; the waveform gets `CueFractions="{Binding CueFractions}"`. Insert before the playlist:

```xml
      <!-- BPM, sync, nudge and hot cues. The phone puts the cues on a line of their own. -->
      <Grid Grid.Row="5" Classes="bpmrow" ColumnDefinitions="Auto,Auto,Auto,Auto,*,Auto" RowDefinitions="Auto,Auto">
        <Button Classes="bpm" Content="{Binding BpmText}"
                ToolTip.Tip="BPM at the current tempo. Tap to correct a track detected at half or double speed.">
          <Button.Flyout>
            <MenuFlyout>
              <MenuItem Header="×½   it's half this" Command="{Binding HalveBpmCommand}" />
              <MenuItem Header="×2   it's double this" Command="{Binding DoubleBpmCommand}" />
            </MenuFlyout>
          </Button.Flyout>
        </Button>
        <Button Grid.Column="1" Classes="tiny" Content="SYNC" Margin="6,0,0,0"
                Command="{Binding SyncCommand}"
                ToolTip.Tip="{Binding SyncHint}" ToolTip.ShowOnDisabled="True" />
        <Button Grid.Column="2" Name="NudgeDown" Classes="tiny nudge" Content="‹" Margin="6,0,0,0"
                ToolTip.Tip="Hold to slow down 4%, to line the beats up by ear" />
        <Button Grid.Column="3" Name="NudgeUp" Classes="tiny nudge" Content="›" Margin="4,0,0,0"
                ToolTip.Tip="Hold to speed up 4%, to line the beats up by ear" />
        <ItemsControl Grid.Column="5" Classes="hotcues" ItemsSource="{Binding HotCues}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
              <StackPanel Orientation="Horizontal" Spacing="4" />
            </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:HotCueSlot">
              <Button Classes="hotcue" Classes.set="{Binding IsSet}" Content="{Binding Label}"
                      BorderBrush="{Binding Index, Converter={StaticResource HotCueBrush}}"
                      Click="OnHotCueClick" ContextRequested="OnHotCueContextRequested"
                      ToolTip.Tip="Tap to set this cue, or jump to it once set. Long-press or right-click to clear it." />
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </Grid>
```

`DeckView.axaml.cs` — add `using System;`, `using Avalonia.Input;`, `using Avalonia.Interactivity;`, and:

```csharp
    private HotCueSlot? _clearedSlot;
    private long _clearedAt;

    private DeckViewModel? Vm => DataContext as DeckViewModel;
```

At the end of the constructor:

```csharp
        HoldToNudge("NudgeDown", -1);
        HoldToNudge("NudgeUp", +1);
```

and the handlers:

```csharp
    /// <summary>
    /// Nudges for as long as the button is held. A Button marks its own press as
    /// handled, so these listen on the tunnel with handledEventsToo.
    /// </summary>
    private void HoldToNudge(string name, int direction)
    {
        Button button = this.FindControl<Button>(name)!;
        button.AddHandler(PointerPressedEvent, (_, _) => Vm?.BeginNudge(direction),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, _) => Vm?.EndNudge(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        button.PointerCaptureLost += (_, _) => Vm?.EndNudge();
    }

    private void OnHotCueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HotCueSlot slot }) return;
        // Lifting the finger after a long-press also clicks. That must not set
        // again the cue the long-press just cleared.
        if (ReferenceEquals(slot, _clearedSlot) && Environment.TickCount64 - _clearedAt < 1500)
        {
            _clearedSlot = null;
            return;
        }
        Vm?.HotCueCommand.Execute(slot);
    }

    /// <summary>Right-click, or a long-press on touch, clears the cue.</summary>
    private void OnHotCueContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: HotCueSlot slot }) return;
        Vm?.ClearHotCueCommand.Execute(slot);
        _clearedSlot = slot;
        _clearedAt = Environment.TickCount64;
        e.Handled = true;
    }
```

- [ ] **Step 5: Styles**

In `Styles.axaml` add `xmlns:vm="using:Mkb.Mixer.App.ViewModels"` and `xmlns:conv="using:Mkb.Mixer.App.Converters"` to the root, `<conv:HotCueBrushConverter x:Key="HotCueBrush" />` inside `<Styles.Resources>`, and:

```xml
  <Style Selector="Grid.bpmrow">
    <Setter Property="Margin" Value="0,8,0,0" />
  </Style>

  <Style Selector="Button.bpm">
    <Setter Property="FontFamily" Value="monospace" />
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Padding" Value="6,2" />
    <Setter Property="MinWidth" Value="88" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
  </Style>

  <Style Selector="Button.hotcue">
    <Setter Property="Width" Value="30" />
    <Setter Property="Height" Value="24" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="FontSize" Value="12" />
    <Setter Property="BorderThickness" Value="2" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
  </Style>

  <Style Selector="Button.hotcue.set" x:DataType="vm:HotCueSlot">
    <Setter Property="Background" Value="{Binding Index, Converter={StaticResource HotCueBrush}}" />
    <Setter Property="Foreground" Value="#0E1013" />
  </Style>
```

In the tablet (`.touch`) section add:

```xml
  <Style Selector=".touch Button.hotcue">
    <Setter Property="Width" Value="40" />
    <Setter Property="Height" Value="34" />
  </Style>

  <Style Selector=".touch Button.nudge">
    <Setter Property="MinWidth" Value="40" />
  </Style>
```

and change `.touch ListBox.playlist` to `MinHeight 48`, `MaxHeight 60` (the new row takes the 36 px the playlist gives up, so the library keeps its share).

In the phone section add:

```xml
  <Style Selector=".phone ItemsControl.hotcues">
    <Setter Property="Grid.Row" Value="1" />
    <Setter Property="Grid.Column" Value="0" />
    <Setter Property="Grid.ColumnSpan" Value="6" />
    <Setter Property="Margin" Value="0,8,0,0" />
  </Style>
```

- [ ] **Step 6: Run the suite and look at all three layouts**

Run: `MKB_UI_SNAPSHOT=<scratchpad>/ui.png dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass. Then open `ui.png`, `mkb-mixer-tablet.png` and `mkb-mixer-phone-0.png` from that directory and check: the BPM row fits inside each deck panel with nothing clipped; cue buttons 1 and 3 on deck A are filled red and blue with matching markers on the waveform; the phone shows the cues on their own line; the tablet library still shows at least three rows. Fix any clipping in `Styles.axaml` before committing.

- [ ] **Step 7: Commit**

```bash
git add src/Mkb.Mixer.App tests/Mkb.Mixer.Tests/UiSmokeTests.cs
git commit -m "Add the BPM, sync, nudge and hot cue row to each deck, with cue markers on the waveform

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Look-ahead for the next track

**Files:**
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (`DequeueNext`, new `PeekNext`, playlist and shuffle hooks)
- Test: `tests/Mkb.Mixer.Tests/LookAheadTests.cs`

**Interfaces:**
- Consumes: `AnalysisQueue.Prefetch` (Task 5).
- Produces: `Track? DeckViewModel.PeekNext()` — the track `DequeueNext()` will return next, chosen once and held until the playlist or shuffle changes.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/LookAheadTests.cs`:

```csharp
using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LookAheadTests
{
    /// <summary>Returns the given picks in turn, so tests can tell a re-roll from a held pick.</summary>
    private sealed class ScriptedRandom(params int[] picks) : Random
    {
        private int _next;
        public override int Next(int maxValue) => Math.Min(picks[_next++ % picks.Length], maxValue - 1);
    }

    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static (DeckViewModel Deck, AnalysisQueue Queue, FakeAudioEngine Engine) Deck(Random? random = null)
    {
        var engine = new FakeAudioEngine();
        var queue = new AnalysisQueue(engine, TrackStore.InMemory(), post: a => a(), manual: true);
        return (new DeckViewModel(new FakeDeck(DeckId.A), engine, random, queue), queue, engine);
    }

    [Fact]
    public void TheShufflePickIsMadeOnceAndKept()
    {
        var (deck, _, _) = Deck(new ScriptedRandom(1, 0, 0, 0));
        foreach (string n in new[] { "a", "b", "c" }) deck.Playlist.Add(T(n));
        deck.Shuffle = true;

        Track peeked = deck.PeekNext()!;
        Assert.Same(peeked, deck.PeekNext());

        Assert.Same(peeked, deck.DequeueNext());
        Assert.Equal("b", peeked.Title);
    }

    [Fact]
    public void RemovingTheHeldTrackPicksAgain()
    {
        var (deck, _, _) = Deck();
        Track a = T("a"), b = T("b");
        deck.Playlist.Add(a);
        deck.Playlist.Add(b);
        Assert.Same(a, deck.PeekNext());

        deck.Playlist.Remove(a);

        Assert.Same(b, deck.DequeueNext());
    }

    [Fact]
    public void ReorderingAnUnshuffledQueuePicksTheNewTop()
    {
        var (deck, _, _) = Deck();
        Track a = T("a"), b = T("b");
        deck.Playlist.Add(a);
        deck.Playlist.Add(b);
        Assert.Same(a, deck.PeekNext());

        deck.Playlist.Move(0, 1);

        Assert.Same(b, deck.PeekNext());
    }

    [Fact]
    public async Task TheNextTrackIsAnalysedAheadOfTime()
    {
        var (deck, queue, engine) = Deck();
        deck.Playlist.Add(T("a"));
        deck.Playlist.Add(T("b"));

        await queue.DrainAsync();

        Assert.Equal(["/m/a.mp3"], engine.AnalysedPaths);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~LookAheadTests"`
Expected: build error, `PeekNext` does not exist.

- [ ] **Step 3: Implement**

In `DeckViewModel.cs` add fields `private Track? _nextUp;` and `private bool _dequeuing;`, and in the constructor:

```csharp
        Playlist.CollectionChanged += (_, _) =>
        {
            if (!_dequeuing) PrefetchNext();
        };
```

Add `partial void OnShuffleChanged(bool value) { _nextUp = null; PrefetchNext(); }`. Replace `DequeueNext` with:

```csharp
    /// <summary>
    /// The track <see cref="DequeueNext"/> will return. Chosen once and held, so
    /// shuffle's random pick is known early enough to analyse it before the
    /// auto-cue needs its start point. Chosen again if it leaves the playlist, or
    /// stops being the top of an unshuffled one.
    /// </summary>
    public Track? PeekNext()
    {
        if (Playlist.Count == 0) return _nextUp = null;
        if (_nextUp is not null && Playlist.Contains(_nextUp) && (Shuffle || Equals(Playlist[0], _nextUp)))
            return _nextUp;

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
        return _nextUp = Playlist[index];
    }

    /// <summary>
    /// Pops the next queued track, honouring shuffle and repeat. The auto-cue and
    /// "play the next track when this one ends" both come through here.
    /// </summary>
    public Track? DequeueNext()
    {
        if (PeekNext() is not { } next) return null;

        _dequeuing = true;   // re-picking mid-way would see a stale _lastDequeued
        try
        {
            Playlist.RemoveAt(Playlist.IndexOf(next));
            if (Repeat) Playlist.Add(next);
        }
        finally { _dequeuing = false; }

        _lastDequeued = next;
        _nextUp = null;
        PrefetchNext();
        return next;
    }

    private void PrefetchNext() => _analysis.Prefetch(PeekNext());
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass, including the existing `ShuffleRepeatTests` unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs tests/Mkb.Mixer.Tests/LookAheadTests.cs
git commit -m "Pick each deck's next track early and analyse it before it is needed

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Auto-cue timed to real sound

**Files:**
- Create: `src/Mkb.Mixer.Audio/TrackTiming.cs`
- Modify: `src/Mkb.Mixer.Audio/AutoCue.cs`, `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (`TimingFor`)
- Test: `tests/Mkb.Mixer.Tests/AutoCueTimingTests.cs`

**Interfaces:**
- Consumes: `TrackStore.Get`, `TrackInfo.HotCueSeconds/FirstSoundSeconds/LastSoundSeconds/DisplayBpm` (Task 4).
- Produces: `sealed record TrackTiming(TimeSpan? Start, TimeSpan? End, double? Bpm)`; `AutoCue(IDeck deckA, IDeck deckB, Func<DeckId, Track?> dequeueNext, Func<Track, TrackTiming?>? timing = null)`; private `MainViewModel.TimingFor(Track)`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/AutoCueTimingTests.cs`:

```csharp
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class AutoCueTimingTests
{
    internal static readonly Track Current = new("/m/now.mp3", "now", Duration: TimeSpan.FromMinutes(4));
    internal static readonly Track Next = new("/m/next.mp3", "next", Duration: TimeSpan.FromMinutes(4));

    internal static (AutoCue Cue, FakeDeck A, FakeDeck B, Dictionary<string, TrackTiming> Timings) Setup()
    {
        var a = new FakeDeck(DeckId.A);
        var b = new FakeDeck(DeckId.B);
        var timings = new Dictionary<string, TrackTiming>();
        bool queued = true;
        Track? Dequeue(DeckId deck)
        {
            // Hands out Next exactly once, to deck B.
            if (deck != DeckId.B || !queued) return null;
            queued = false;
            return Next;
        }
        var cue = new AutoCue(a, b, Dequeue, t => timings.GetValueOrDefault(t.Path))
        {
            Enabled = true,
            CrossfadeDuration = TimeSpan.FromSeconds(20)
        };
        a.Load(Current);
        a.Play();
        return (cue, a, b, timings);
    }

    [Fact]
    public void TheFadeIsTimedToTheLastSound()
    {
        var (cue, a, _, timings) = Setup();
        timings[Current.Path] = new TrackTiming(null, TimeSpan.FromSeconds(210), null);

        a.Position = TimeSpan.FromSeconds(189);
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Idle, cue.State);

        a.Position = TimeSpan.FromSeconds(190);
        cue.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoCueState.Transitioning, cue.State);
    }

    [Fact]
    public void ASpedUpTrackStartsItsFadeOnTime()
    {
        // 25 s of track at 1.25x is 20 s of real time.
        var (cue, a, _, _) = Setup();
        a.Tempo = 1.25f;

        a.SetRemaining(TimeSpan.FromSeconds(26));
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Idle, cue.State);

        a.SetRemaining(TimeSpan.FromSeconds(25));
        cue.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoCueState.Transitioning, cue.State);
    }

    [Fact]
    public void TheIncomingTrackStartsAtItsStartPoint()
    {
        var (cue, a, b, timings) = Setup();
        timings[Next.Path] = new TrackTiming(TimeSpan.FromSeconds(4.5), null, null);
        a.SetRemaining(TimeSpan.FromSeconds(10));

        cue.Tick(TimeSpan.Zero);

        Assert.Same(Next, b.Track);
        Assert.Equal(TimeSpan.FromSeconds(4.5), b.Position);
    }

    [Fact]
    public void WithoutAStartPointItStartsFromTheTop()
    {
        var (cue, a, b, _) = Setup();
        a.SetRemaining(TimeSpan.FromSeconds(10));

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, b.Position);
    }

    [Fact]
    public void SeekingPastTheLastSoundStartsTheFadeAtOnce()
    {
        var (cue, a, _, timings) = Setup();
        timings[Current.Path] = new TrackTiming(null, TimeSpan.FromSeconds(210), null);
        cue.CrossfaderPosition = 0.2f;

        a.Position = TimeSpan.FromSeconds(225);
        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Equal(0.2f, cue.CrossfaderPosition, 3);
    }

    [Fact]
    public async Task TheMixerBringsTheNextTrackInAtHotCueOne()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        store.Update(Next.Path, i => i.WithHotCue(0, 12) with { FirstSoundSeconds = 3 });
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")),
            store, post: a => a(), manualAnalysis: true)
        {
            CrossfaderPosition = 0f,
            AutoCueEnabled = true,
            CrossfadeSeconds = 20
        };
        vm.DeckB.Playlist.Add(Next);
        await vm.DeckA.LoadAsync(Current);
        await vm.DeckA.PlayCommand.ExecuteAsync(null);
        engine.A.SetRemaining(TimeSpan.FromSeconds(15));

        vm.Tick(TimeSpan.Zero);

        Assert.Same(Next, engine.B.Track);
        Assert.Equal(TimeSpan.FromSeconds(12), engine.B.Position);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~AutoCueTimingTests"`
Expected: build error, `TrackTiming` does not exist.

- [ ] **Step 3: Implement**

`src/Mkb.Mixer.Audio/TrackTiming.cs`:

```csharp
namespace Mkb.Mixer.Audio;

/// <summary>What the auto-cue needs to know about a track, from whatever remembers it.</summary>
/// <param name="Start">Where to bring the track in: hot cue 1, else its first sound.</param>
/// <param name="End">Its last real sound.</param>
/// <param name="Bpm">Its BPM at normal speed, after any user correction.</param>
public sealed record TrackTiming(TimeSpan? Start, TimeSpan? End, double? Bpm);
```

`AutoCue.cs` — change the primary constructor and `Tick`, and add two helpers:

```csharp
/// <param name="timing">Start, end and BPM per track, if known. Without it the
/// auto-cue fades on the file's length and starts tracks from the top.</param>
public sealed class AutoCue(
    IDeck deckA,
    IDeck deckB,
    Func<DeckId, Track?> dequeueNext,
    Func<Track, TrackTiming?>? timing = null)
```

In `Tick`, replace `if (active is null || active.Remaining > CrossfadeDuration)` with `if (active is null || WallTimeLeft(active) > CrossfadeDuration)`, and replace `incoming.Load(next); incoming.Play();` with:

```csharp
        incoming.Load(next);
        incoming.Play();
        // After Play: some backends restart a stopped player from the top.
        if (Timing(next)?.Start is { } start && start > TimeSpan.Zero)
            incoming.Seek(start);
```

Add:

```csharp
    /// <summary>
    /// Real time until the deck's last real sound. Divided by tempo: the original
    /// compared track time with the fade length, so a sped-up track faded late.
    /// </summary>
    private TimeSpan WallTimeLeft(IDeck deck)
    {
        TimeSpan end = deck.Duration;
        if (deck.Track is { } t && Timing(t)?.End is { } last && (end <= TimeSpan.Zero || last < end))
            end = last;
        float tempo = deck.Tempo > 0 ? deck.Tempo : 1f;
        return (end - deck.Position) / tempo;
    }

    private TrackTiming? Timing(Track track) => timing?.Invoke(track);
```

Update the class `<summary>`/`<remarks>` with one sentence: "Given track timings it fades to finish at the outgoing track's last real sound and brings the next one in at its first."

- [ ] **Step 4: Wire it to the store**

In `MainViewModel`, pass `TimingFor` as the fourth argument to `new AutoCue(...)` and add:

```csharp
    /// <summary>Hot cue 1 beats the detected first sound as the place to bring a track in.</summary>
    private TrackTiming? TimingFor(Track track)
    {
        if (_tracks.Get(track.Path) is not { } info) return null;
        return new TrackTiming(
            Seconds(info.HotCueSeconds[0] ?? info.FirstSoundSeconds),
            Seconds(info.LastSoundSeconds),
            info.DisplayBpm);

        static TimeSpan? Seconds(double? s) => s is { } v ? TimeSpan.FromSeconds(v) : null;
    }
```

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass, including the existing `AutoCueTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Mkb.Mixer.Audio src/Mkb.Mixer.App/ViewModels/MainViewModel.cs tests/Mkb.Mixer.Tests/AutoCueTimingTests.cs
git commit -m "Time the auto-cue fade to real sound, start tracks at hot cue 1, and allow for tempo

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Tempo matching during auto-cue

**Files:**
- Modify: `src/Mkb.Mixer.Audio/TrackTiming.cs` (enum), `src/Mkb.Mixer.Audio/AutoCue.cs`, `src/Mkb.Mixer.Library/Settings.cs`, `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs` (tempo echo), `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs`, `src/Mkb.Mixer.App/Views/MainView.axaml`, `src/Mkb.Mixer.App/Views/PhoneView.axaml`
- Test: `tests/Mkb.Mixer.Tests/TempoMatchTests.cs`

**Interfaces:**
- Consumes: `AutoCueTimingTests.Setup/Current/Next` (Task 13), `TrackTiming.Bpm`.
- Produces: `enum TempoMatchMode { Off, Match, MatchAndGlide }`; on `AutoCue`: `TempoMatchMode TempoMatch`, `const double MaxTempoMatch = 0.08`, `static readonly TimeSpan GlideDuration` (8 s), `void CancelGlide(DeckId deck)`; `DeckViewModel.UserChangedTempo` (`event EventHandler?`); `AppSettings.AutoCueTempoMatch`; on `MainViewModel`: observable `TempoMatchMode TempoMatch` and `int TempoMatchIndex`.

- [ ] **Step 1: Write the failing tests**

`tests/Mkb.Mixer.Tests/TempoMatchTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~TempoMatchTests"`
Expected: build error, `TempoMatchMode` does not exist.

- [ ] **Step 3: Implement in AutoCue**

Append to `TrackTiming.cs`:

```csharp
/// <summary>How the auto-cue treats tempo across a transition.</summary>
public enum TempoMatchMode
{
    /// <summary>Each track plays at whatever tempo its deck is set to.</summary>
    Off,
    /// <summary>The incoming track takes the outgoing track's BPM, and keeps it.</summary>
    Match,
    /// <summary>As Match, then eases back to normal speed once the fade is done.</summary>
    MatchAndGlide
}
```

In `AutoCue.cs` add:

```csharp
    /// <summary>Tempo matching only bends a track this far; further apart, it would sound wrong.</summary>
    public const double MaxTempoMatch = 0.08;

    public static readonly TimeSpan GlideDuration = TimeSpan.FromSeconds(8);

    public TempoMatchMode TempoMatch { get; set; }

    private IDeck? _incoming;
    private bool _matched;
    private IDeck? _glideDeck;
    private float _glideFrom;
    private TimeSpan _glideStartedAt;

    /// <summary>The user has taken over this deck's tempo: stop any glide on it, now or after the fade.</summary>
    public void CancelGlide(DeckId deck)
    {
        if (_glideDeck?.Id == deck) _glideDeck = null;
        if (_incoming?.Id == deck) _matched = false;
    }

    private bool TryMatchTempo(IDeck outgoing, IDeck incoming, Track next)
    {
        if (TempoMatch == TempoMatchMode.Off) return false;
        double? outBpm = outgoing.Track is { } t ? Timing(t)?.Bpm : null;
        double? inBpm = Timing(next)?.Bpm;
        if (outBpm is not > 0 || inBpm is not > 0) return false;

        double ratio = outBpm.Value * outgoing.Tempo / inBpm.Value;
        if (Math.Abs(ratio - 1) > MaxTempoMatch) return false;
        incoming.Tempo = (float)ratio;
        return true;
    }

    private void AdvanceGlide(TimeSpan now)
    {
        if (_glideDeck is null) return;
        double t = Math.Clamp((now - _glideStartedAt) / GlideDuration, 0, 1);
        _glideDeck.Tempo = t >= 1 ? 1f : (float)(_glideFrom + (1 - _glideFrom) * t);
        if (t >= 1) _glideDeck = null;
    }
```

In `Tick`: make `AdvanceGlide(now);` the very first statement (a glide finishes even if auto-cue is switched off), and set the tempo before playing:

```csharp
        incoming.Load(next);
        _matched = TryMatchTempo(active, incoming, next);
        _incoming = incoming;
        incoming.Play();
```

In `AdvanceFade`, after `State = AutoCueState.Idle;`:

```csharp
        if (_matched && TempoMatch == TempoMatchMode.MatchAndGlide && _incoming is not null)
        {
            _glideDeck = _incoming;
            _glideFrom = _incoming.Tempo;
            _glideStartedAt = now;
        }
        _incoming = null;
        _matched = false;
```

- [ ] **Step 4: Deck tempo echo**

In `DeckViewModel.cs` add:

```csharp
    /// <summary>Raised when the user, not the auto-cue, changes this deck's tempo.</summary>
    public event EventHandler? UserChangedTempo;

    private bool _echoingTempo;
```

Replace `OnTempoChanged`:

```csharp
    partial void OnTempoChanged(double value)
    {
        if (!_echoingTempo)
        {
            _deck.Tempo = (float)value;
            UserChangedTempo?.Invoke(this, EventArgs.Empty);
        }
        OnPropertyChanged(nameof(TempoLabel));
        OnPropertyChanged(nameof(HeardBpm));
        OnPropertyChanged(nameof(BpmText));
    }
```

In `Refresh()`, right after the track-change check, add:

```csharp
        if (Math.Abs(_deck.Tempo - Tempo) > 0.0005)
        {
            // The auto-cue moved it. Follow it without treating that as the user's doing.
            _echoingTempo = true;
            Tempo = _deck.Tempo;
            _echoingTempo = false;
        }
```

- [ ] **Step 5: Setting, view model and views**

`Settings.cs`: add `public TempoMatchMode AutoCueTempoMatch { get; set; }` to `AppSettings`.

`MainViewModel.cs`:

```csharp
    [ObservableProperty] private TempoMatchMode _tempoMatch;

    /// <summary>For the combo box, whose items are in enum order.</summary>
    public int TempoMatchIndex
    {
        get => (int)TempoMatch;
        set => TempoMatch = (TempoMatchMode)Math.Clamp(value, 0, 2);
    }

    partial void OnTempoMatchChanged(TempoMatchMode value)
    {
        _autoCue.TempoMatch = value;
        OnPropertyChanged(nameof(TempoMatchIndex));
    }
```

In the constructor, after `_autoCue` is created: `TempoMatch = _settings.AutoCueTempoMatch;`, and

```csharp
        DeckA.UserChangedTempo += (_, _) => _autoCue.CancelGlide(DeckId.A);
        DeckB.UserChangedTempo += (_, _) => _autoCue.CancelGlide(DeckId.B);
```

In `SaveState()`: `_settings.AutoCueTempoMatch = TempoMatch;`.

`MainView.axaml` — mixer grid `ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto"`; insert after the fade `NumericUpDown`:

```xml
        <TextBlock Grid.Column="6" Classes="dim" Text="match tempo" VerticalAlignment="Center"
                   Margin="16,0,6,0" />
        <ComboBox Grid.Column="7" SelectedIndex="{Binding TempoMatchIndex}" MinWidth="130"
                  ToolTip.Tip="Off: each track plays at its own speed. Match: the incoming track is sped up or slowed to the outgoing one's BPM, if they are within 8%. Match + glide: then it eases back to normal speed over 8 seconds.">
          <ComboBoxItem>off</ComboBoxItem>
          <ComboBoxItem>match</ComboBoxItem>
          <ComboBoxItem>match + glide</ComboBoxItem>
        </ComboBox>
```

Move the clock `TextBlock` to `Grid.Column="8"` and give the headphone `StackPanel` `Grid.ColumnSpan="9"`.

`PhoneView.axaml` — the mixer panel grid becomes `RowDefinitions="Auto,Auto,Auto,Auto"`; move the headphone `WrapPanel` to `Grid.Row="3"` and insert:

```xml
                <StackPanel Grid.Row="2" Grid.ColumnSpan="3" Orientation="Horizontal"
                            HorizontalAlignment="Center" Spacing="8" Margin="0,6,0,0">
                  <TextBlock Classes="dim" Text="match tempo" VerticalAlignment="Center" />
                  <ComboBox SelectedIndex="{Binding TempoMatchIndex}" MinWidth="140">
                    <ComboBoxItem>off</ComboBoxItem>
                    <ComboBoxItem>match</ComboBoxItem>
                    <ComboBoxItem>match + glide</ComboBoxItem>
                  </ComboBox>
                </StackPanel>
```

- [ ] **Step 6: Run the suite and look at the layouts**

Run: `MKB_UI_SNAPSHOT=<scratchpad>/ui.png dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass. Check `ui.png` (mixer row fits at 1400 px) and `mkb-mixer-phone-0.png` (the new row is centred and nothing clips).

- [ ] **Step 7: Commit**

```bash
git add src tests/Mkb.Mixer.Tests/TempoMatchTests.cs
git commit -m "Optionally match tempo across auto-cue transitions, and glide back afterwards

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: README and final verification

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the features**

After the "Shuffle and repeat" section, add:

```markdown
## Library

Tap a column header to sort; Time and BPM sort by value. A ✓ marks tracks already
played this session, and **★ Recently played** at the top of the folder tree lists
the last 100 tracks played, newest first. A track counts as played after 30
seconds, or when it ends.

With **analyse BPM** on, opening a folder works out each track's BPM in the
background, one track at a time, and the column fills in as it goes. Results are
kept, so each file is only analysed once unless it changes. It is on by default on
desktops and tablets and off on phones, where it costs battery the first time a big
folder is opened.

**BPM [ ]–[ ]** lists only tracks in that range; **≈** fills it with the playing
deck's BPM ±6%, which is roughly what will mix with it.

## BPM and sync

Each deck shows its track's BPM at the current tempo. BPM detection folds results
into 87.5–175, so a 75 BPM track reads as 150: tap the BPM to correct it with ×½ or
×2, and the correction is kept for that track.

**SYNC** sets this deck's tempo so it plays at the other deck's BPM. It matches
speed, not beat position: hold **‹** or **›** to slow down or speed up by 4% while
lining the beats up by ear.

## Hot cues

**1–4** on each deck: tap an empty one to mark the playhead, tap a set one to jump
there (playing stays playing; stopped stays stopped at the cue). Long-press, or
right-click with a mouse, to clear one. Cues show on the waveform and are kept per
track.

## Smart auto-cue

The fade is timed to finish at the outgoing track's last real sound rather than the
end of the file, so trailing silence and long near-silent fade-outs are skipped.
The incoming track starts at hot cue 1 if it has one, otherwise at its first sound.
Each deck's next track is picked and analysed ahead of time so this information is
ready when the fade begins.

**match tempo** under the crossfader decides what happens to speed:

| Mode | What it does |
|---|---|
| **off** | Each track plays at its own speed. |
| **match** | The incoming track is sped up or slowed to the outgoing track's BPM, and stays there. |
| **match + glide** | As match, then eases back to normal speed over 8 seconds once the fade is done. |

Matching only happens when both BPMs are known and within 8% of each other;
otherwise that transition plays both at their own speed.
```

In "**Added**" add:

```markdown
- BPM detection, SYNC and nudge; hot cues; play history and a BPM filter in the library.
- An auto-cue that fades on real sound and can match tempo across the transition.
```

In "Known limitations" add:

```markdown
- **SYNC matches tempo, not beat position.** Beat positions are detected and stored,
  ready for phase sync, but nothing uses them yet; line beats up with the nudge buttons.
- **BPM detection folds into 87.5–175.** Slower or faster tracks read at double or
  half; correct them once with ×½ or ×2.
```

- [ ] **Step 2: Full verification**

Run: `MKB_UI_SNAPSHOT=<scratchpad>/ui.png dotnet test tests/Mkb.Mixer.Tests`
Expected: all pass; no warnings introduced in the build output (`dotnet build 2>&1 | grep -c warning` before and after).

Open the four renders (`ui.png`, `mkb-mixer-tablet.png`, `mkb-mixer-phone-0.png`, `mkb-mixer-phone-2.png`) and check nothing is clipped. Then walk the spec section by section and confirm each requirement has landed; note anything that did not in the PR description.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Document library history, BPM sync, hot cues and smart auto-cue

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
