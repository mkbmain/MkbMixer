# Headphone cue, shuffle/repeat, Android media session — design

*2026-10-04*

## Context

Three gaps from the feature review, tackled together on `cue-shuffle-media-session`:

1. **Android media session and audio focus.** Headset, Bluetooth and lock-screen
   buttons do nothing; nothing pauses when the output device goes away or another
   music app starts.
2. **Shuffle and repeat.** The auto-cue always pulls the top of the incoming deck's
   queue, and the mix stops when the queue empties.
3. **Headphone cue (pre-listen).** There is one output, so the next track cannot be
   heard before the room hears it. This is the biggest gap for live use.

The intended use is live DJing from a laptop, tablet or phone. The room mix must
not be interrupted by anything incidental (a text arriving, a watch disconnecting),
but must stop when the output actually goes away or another music app takes over.

## 1. Headphone cue

### Approach

Each deck gets a `CueTap` modifier on its SoundFlow player. `SoundComponent.Process`
runs `GenerateAudio` → modifiers → volume/pan → mix into parent → analyzers
(verified from the 1.4.1 IL), so a modifier sees the deck's audio **before** the
crossfader gain and the mute (mute is applied as `Volume = 0`, not SoundFlow's
`Mute`, which would skip processing entirely). Each cued tap adds its block into a
shared `CueBus`. Tracks are decoded once and the cue is sample-aligned with the
master.

Rejected: a second player per deck on the cue device (double decode, drift, re-sync
on every seek/tempo change); a single 4-channel output (needs a DJ interface).

### Modes

| Mode | Master output | Headphones |
|---|---|---|
| **Off** (default) | Normal stereo | — |
| **Split** | Left channel: master folded to mono | Right channel: headphone blend folded to mono |
| **Device** | Normal stereo, unchanged | A second playback device, stereo headphone blend |

Off is the default because Split turns the room mix into mono on one channel.

**Split** — a `CueSplitModifier` on the master mixer. The master mixer processes its
decks (whose taps fill the bus) and then runs its own modifiers, all in the same
audio callback, so the modifier reads exactly the block the taps just wrote. It
writes L = mono(master), R = mono(blend), then clears the bus.

**Device** — a `CueFeedModifier` on the master mixer computes the blend and writes it
into a `CueRingBuffer` without altering the master. A `CueSource` component in the
second device's master mixer reads the ring buffer. The two devices run on separate
clocks, so the ring buffer is single-producer/single-consumer and lock-free:

- the writer drops incoming samples when full;
- the reader pads with silence when empty;
- the reader skips ahead when the fill exceeds a high-water mark, keeping latency
  bounded (target ≈ one device period, 20–40 ms; capacity ≈ 200 ms).

### Cue/master blend

`CueMix`: 0 = cue only, 1 = master only. The blend reuses `Crossfader.Gains` so it
is constant-power like the main fader. Applies in Split and Device modes.

### Interfaces

```csharp
public enum CueMode { Off, Split, Device }

// IDeck
bool IsCued { get; set; }          // routes this deck into the cue bus

// IAudioEngine
CueMode CueMode { get; }
string? CueDevice { get; }         // name of the open cue device, Device mode only
float CueMix { get; set; }         // 0 = cue only, 1 = master only
IReadOnlyList<string> CueDeviceNames();   // real playback devices, dummies excluded
bool TrySetCue(CueMode mode, string? deviceName, out string? error);
```

`TrySetCue` closes any existing cue device first. If the cue device fails to open,
the engine stays in Off and returns the reason; the app shows it in the status bar.
With no audio output at all, every mode except Off fails.

### Units

| Unit | Project | Depends on | Tested without a sound card |
|---|---|---|---|
| `CueBus` — accumulates cued blocks for one callback | Audio | — | yes |
| `CueMixing` — blend, mono fold, split interleave (pure span maths) | Audio | `Crossfader` | yes |
| `CueRingBuffer` — SPSC float ring with drop/pad/skip-ahead | Audio | — | yes |
| `CueTap`, `CueSplitModifier`, `CueFeedModifier`, `CueSource` — thin SoundFlow adapters | Audio | the above + SoundFlow | integration only |

`SoundFlowDeck` owns one `CueTap` for its lifetime and adds it to each new player in
`Load`. `IsCued` is a volatile flag on the tap.

## 2. Android media session and audio focus

### Controls

`PlaybackService` gains an `android.media.session.MediaSession` (framework API, no new
package). The notification becomes `Notification.MediaStyle` bound to the session
token, with a single play/pause action. Headset buttons, Bluetooth buttons and the
lock screen all route through the session. No next/previous.

The behaviour lives in `MainViewModel`, platform-neutral and unit-tested:

- `PauseAll()` pauses every playing deck and remembers which ones it paused.
- `ResumePaused()` resumes the remembered decks that are still paused, then forgets
  them. If none are remembered, it plays the deck in front on the crossfader
  (A when position ≤ 0.5), exactly as that deck's play button would.

### Audio focus

Requested when playback starts, with `SetWillPauseWhenDucked(true)` so Android does
not duck the mix on our behalf.

| Event | Response |
|---|---|
| `AUDIOFOCUS_LOSS` — another app starts playing music | `PauseAll()`; no auto-resume |
| `AUDIOFOCUS_LOSS_TRANSIENT` while the audio mode is ringtone, in-call or in-communication | `PauseAll()`, then `ResumePaused()` on `AUDIOFOCUS_GAIN` |
| Any other transient loss, or may-duck (voice note, video, navigation prompt, text alert) | Ignore: keep playing at full volume |
| `ACTION_AUDIO_BECOMING_NOISY` | `PauseAll()` |

`ACTION_AUDIO_BECOMING_NOISY` is deliberately the only disconnect signal. Android
sends it only when the route **currently carrying the app's audio** (wired
headphones, a splitter, a Bluetooth speaker or headphones) goes away and audio is
about to fall back to the speaker. A Bluetooth device that is not the audio output —
a watch, a car kit — disconnecting does not send it, so the mix carries on. Do not
replace it with a general Bluetooth-disconnect listener.

Switching to another app (e.g. to reply to a text) does not involve audio focus; the
existing foreground service keeps the mix running.

### Service lifetime

Today the service stops 10 s after both decks fall silent. After a pause from the
media session, audio focus or becoming-noisy, it is instead held for **10 minutes**
with the play button showing, since Android 12+ will not let the app restart a
foreground service from the background. A pause from the app's own buttons keeps the
10 s behaviour. Resuming clears the hold.

## 3. Shuffle and repeat

Per-deck toggles on `DeckViewModel`: `Shuffle` and `Repeat`. Both the auto-cue and
"play next when a track ends" go through `DequeueNext()`, which is the only method
whose behaviour changes:

- **Shuffle** — take a random queue entry instead of the first. The `Random` is
  injectable so tests are deterministic.
- **Repeat** — the dequeued track is appended back to the end of the queue, so the
  queue never empties.
- **Both** — shuffle never picks the track it dequeued last time, unless it is the
  only one queued, so the same song does not play twice running.

## UI

- **`DeckView`** (shared by desktop, tablet and the phone's Mix tab): `⇄` shuffle,
  `↻` repeat and `🎧 CUE` toggles beside the mute indicator. CUE is disabled while
  the cue mode is Off.
- **Mixer panel**, under the crossfader on both `MainView` and `PhoneView`: a
  *Headphones* row with a mode dropdown (Off / Split / Device), a device dropdown
  visible only in Device mode, and a cue/master slider.

## Settings

`AppSettings` gains `DeckAShuffle`, `DeckARepeat`, `DeckBShuffle`, `DeckBRepeat`,
`CueMode`, `CueDevice` and `CueMix`. On start the saved cue mode is re-applied; if
Device mode's device cannot be opened, the app falls back to Off and says so in the
status bar. Per-deck `IsCued` is not persisted.

## Testing

- `CueMixing`: blend endpoints and constant power, mono fold, split channel layout.
- `CueBus`: only cued decks contribute; clear between blocks.
- `CueRingBuffer`: pad on underrun, drop on overrun, skip-ahead above high water,
  wrap-around.
- Shuffle/repeat with a seeded `Random`, including the no-immediate-repeat rule and
  the single-track case.
- `PauseAll`/`ResumePaused` against `FakeDeck`, including a deck the user restarted
  in between and the nothing-remembered fallback.
- Settings round-trip for the new fields.
- The existing headless render test extended to cover the new controls' bindings.
- Android media session, focus and becoming-noisy are verified on a device; there is
  no Android test host.

## Out of scope

Next/previous media actions and per-deck cue persistence. Device mode on Android
lists whatever AAudio exposes; most phones route to one output at a time, so Split is
the supported phone mode and Device mode there is best-effort. Every other item from
the feature review is deferred.
