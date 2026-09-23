# MKB Music Mixer — rewrite design

*2026-09-23*

## Context

`mp3multiplayer` is a .NET 2.0 WinForms application written around 2005. Despite
the name it is not an MP3 player but a dual-deck DJ mixer: two independent players,
a crossfader, per-deck speed and mute, an auto-cue that mixes the next track in as
the current one ends, two playlists, and a drive browser with search.

The whole application is `Form1.cs` (783 lines) and `Program.cs`. The vendored
`naudio/` folder is an unmodified copy of the NAudio library and is not used by the
app — the app references `WMPLib` instead.

### What makes it Windows-only

| Dependency | Where |
|---|---|
| `WMPLib.WindowsMediaPlayer` COM | Both decks |
| `DriveInfo.GetDrives()` drive letters | Browser roots |
| Hardcoded `\` separators | `addNode`, `textBox1_TextChanged`, `treeView1_AfterSelect` |
| `System.Windows.Forms` | Everything |
| `Process.GetCurrentProcess().Kill()` | `Form1_FormClosing` |

## Decisions

| Decision | Choice | Why |
|---|---|---|
| UI framework | Avalonia 12.1.3 | Requested; genuinely cross-platform |
| Runtime | .NET 10 | Current LTS; .NET 8 leaves support Nov 2026 |
| Audio | SoundFlow 1.4.1 (miniaudio) | MIT, natives for all desktop targets, gives sample access for waveforms |
| Tags | TagLibSharp | De facto standard |
| MVVM | CommunityToolkit.Mvvm | Source-generated observables |

### Audio engine

Three options were considered. ManagedBass has the best DJ feature set but is only
free for non-commercial use. LibVLCSharp has the widest codec support but exposes
no sample data, so waveforms would be impossible, and it ships a large native
runtime. SoundFlow was chosen: MIT licensed, it ships miniaudio natives for
Windows (x64/x86/arm64), macOS (x64/arm64), Linux (x64/arm/arm64), FreeBSD, Android
and iOS, and it exposes decoded samples.

This was validated by spike before committing to it:

- `MiniAudioEngine` initialises on Linux and selects the PulseAudio backend
- A 5:48 MP3 decodes with an exact duration and yields full sample data in 366 ms
- Two `SoundPlayer`s sum through a `Mixer`; per-deck `Volume` works as a crossfader
- `Seek(TimeSpan.FromSeconds(2))` lands at exactly `Time == 2.000`
- A 440 Hz tone played at 0.75x, 1.0x and 1.5x measured 439 Hz at every speed,
  confirming `PlaybackSpeed` always time-stretches rather than resampling

Audible output could not be verified: the development container has no `/dev/snd`.

## Architecture

```
Mkb.Mixer.App  ──▶ Mkb.Mixer.Library ──▶ Mkb.Mixer.Audio
      └──────────────────────────────────────┘
```

`Mkb.Mixer.Audio` owns the domain: `Track`, `IDeck`, `IAudioEngine`, `Crossfader`,
`AutoCue`, `Waveform`, and the SoundFlow implementation of the first two. Nothing
outside this project references SoundFlow, so replacing it means writing one class.

`Mkb.Mixer.Library` handles the filesystem: scanning, tags, M3U, settings. It
depends on `Mkb.Mixer.Audio` only for the `Track` record.

> Deviation from the original plan: `Library` was specified as having no dependency
> on `Audio`. Sharing the `Track` record is simpler than introducing a fifth project
> for one type, and the dependency runs in the safe direction.

`Crossfader` and `AutoCue` are pure logic over `IDeck`, so both are tested against a
`FakeDeck` with no audio device.

## Bug fixes carried by the design

### Crossfader

The original, at fader value `v`:

```csharp
if (trackBar1.Value <= 50) player1.volume = (100 - v) * 2;  // v=0  -> 200
else                       player1.volume = 100 - v;         // v=51 -> 49
```

Deck A held full gain across the entire left half, then dropped discontinuously to
0.49 at the midpoint, and the doubled term overflowed past the valid range.

Replaced with a constant-power curve, `gainA = cos(x·π/2)`, `gainB = sin(x·π/2)`,
so `a² + b² = 1` everywhere. Tests assert continuity across 1000 steps, that gains
stay within 0..1, and that power is constant.

### Auto-cue

The original triggered on `timeleft_player1 == 20` from a 1050 ms timer, so a tick
landing on 21 then 19 missed the transition entirely. It also used `&` for `&&` and
slammed the fader to `1` while a separate ±5-per-tick fade was running.

Replaced with a two-state machine — `Idle` and `Transitioning` — triggering on
`remaining >= threshold` and interpolating the fader on elapsed wall-clock time.
Tests cover the skipped-tick case, non-retriggering, irregular tick spacing, and
both fade directions.

### Library scanning

`Application.DoEvents()` inside a recursive UI-thread scan is replaced by a
cancellable `IAsyncEnumerable<string>`.

## Testing

58 tests: crossfade curve, auto-cue state machine, format filter, M3U round-trip,
settings persistence and corruption handling, folder scanning and cancellation,
waveform reduction, plus two headless Avalonia tests that render the real window
and catch XAML binding errors a compile cannot.

## Known limitations

- WMA decodes only on Windows.
- Tempo always preserves pitch; the original's pitch-shifting speed is not
  reproducible without adding a resampling path.
- Audible output is unverified in this environment.
