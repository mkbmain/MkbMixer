# Library sort/history, BPM and sync, hot cues, smart auto-cue — design

*2026-10-08*

## Context

Four features, tackled together on `library-bpm-hotcues-smart-autocue`:

1. **Library.** Column sorting, a ✓ on tracks played this session, a persistent
   "Recently played" list, and a BPM column with a BPM-range filter.
2. **BPM detection and sync.** Detect each track's tempo, show it on the deck, and
   let one deck match the other's tempo. Beat positions are stored so phase sync
   can follow later without reworking the data model.
3. **Hot cues.** Four per deck, saved per track.
4. **Smart auto-cue.** Time the fade to the outgoing track's last real sound, start
   the incoming track at its first sound (or hot cue 1), and optionally match tempo.

The app is no longer built for one user's habits. It must suit both hands-off use
(load playlists, turn auto-cue on, walk away) and hands-on mixing, so behaviour
that only one of those wants sits behind a setting.

All four share a foundation: a per-track analysis pass and a per-track store.

## 1. Foundation: analysis and the track store

### Units

| Type | Project | Responsibility |
|---|---|---|
| `TrackAnalysis` | Audio | Result of one decode: `Waveform`, `Bpm?`, `BeatOffset?`, `FirstSound?`, `LastSound?` |
| `TempoDetector` | Audio | Pure: mono samples + rate → `(Bpm?, BeatOffset?)` |
| `SilenceDetector` | Audio | Pure: mono samples + rate → `(FirstSound?, LastSound?)` |
| `TrackTiming` | Audio | `Start?`, `End?`, `Bpm?` — what `AutoCue` needs about a track, with no Library dependency |
| `TrackInfo` | Library | Persisted per-track record (below) |
| `TrackStore` | Library | Loads/saves `tracks.json` beside `settings.json`, keyed by full path |
| `AnalysisQueue` | App | Single background worker that runs analyses by priority and writes results to `TrackStore` |

### Interface change

`IAudioEngine.AnalyseAsync` returns `TrackAnalysis` instead of `Waveform`. It still
decodes once; the detectors run on the same samples after the waveform reduction.
`FakeAudioEngine` is updated to return a configurable `TrackAnalysis`.

### TrackInfo

```
FileSize, LastWriteUtc        // invalidation: a mismatch discards the analysis fields
Bpm?, BeatOffsetSeconds?, FirstSoundSeconds?, LastSoundSeconds?
BpmMultiplier                 // 1, 0.5 or 2 — the user's ×½/×2 correction, survives re-analysis
HotCueSeconds[4]              // null per empty slot
LastPlayedUtc?, PlayCount
```

The displayed BPM is `Bpm × BpmMultiplier`. Hot cues and play history are kept when
a file's size or timestamp changes; only the analysis fields are discarded.

### TrackStore

- In-memory dictionary, loaded once at start-up.
- Saves debounced ~2 s after the last change, and synchronously on shutdown
  (alongside `MainViewModel`'s existing settings save).
- A corrupt or unreadable file yields an empty store, never an exception, matching
  `SettingsStore`. Writes go to a temp file then move over, so a crash mid-write
  cannot corrupt the store.
- Thread-safe: `AnalysisQueue` writes from its worker, the UI reads on the UI thread.
- Waveform peaks are **not** persisted (2000 floats per track). A deck load still
  decodes to draw the waveform; a cached BPM shows immediately meanwhile.

### AnalysisQueue

Priorities, highest first:

1. A track just loaded on a deck (needs the waveform too).
2. Each deck's "next up" track (see §4).
3. Library folder tracks, when *analyse BPM* is on.

Requests are de-duplicated by path; a cached, still-valid `TrackInfo` is skipped
(except priority 1, which always decodes for the waveform). Opening another folder
drops pending priority-3 items. One worker at a time, so background analysis never
competes with itself for CPU. Results are raised as an event marshalled to the UI
thread, so library rows and decks update live.

### Detection

Before detection, samples are downmixed to mono and decimated to ~11 kHz.

**SilenceDetector.** RMS in 50 ms windows. `FirstSound` is the first window above
−45 dBFS. `LastSound` is the last window above −45 dBFS, then pulled back to where
the level falls below −30 dBFS if that happens within the final 10 s, so a long
near-silent fade-out counts as the end. A track that never crosses the threshold
returns nulls.

**TempoDetector.**

1. Onset envelope: positive energy difference between ~10 ms frames.
2. Autocorrelation across lags equivalent to 70–180 BPM; the strongest peak wins.
3. Octave folding into 85–170 BPM. The user corrects outliers with ×½/×2.
4. Confidence gate: if the peak is not clearly above the mean of the searched range,
   `Bpm` is null. A wrong BPM is worse than none, because sync and auto-cue act on it.
5. `BeatOffset`: the phase within one beat period that best aligns with onsets.
   Stored only; unused until phase sync.

## 2. Library

- **Rows.** The track grid binds to `LibraryRow` view models wrapping `Track`, with
  observable `Bpm` and `IsPlayed`. Commands that act on the selection unwrap
  `row.Track`.
- **Columns.** ✓ · Title · Artist · Album · BPM · Time. Played rows are dimmed.
- **Sorting.** Header tap toggles ascending/descending. Time sorts by `Duration`
  (not the `m:ss` text); BPM sorts numerically with unknowns last.
- **Played.** A track counts once it has played 30 s, or reached its end, on either
  deck. That updates `LastPlayedUtc` and `PlayCount` in the store and adds it to the
  in-memory session set behind ✓.
- **Recently played.** A fixed entry at the top of the folder tree listing the 100
  most recently played tracks, newest first, skipping files that no longer exist.
- **BPM filter.** `BPM [ ]–[ ]` next to the search box, plus **≈**, which fills the
  range with the audible deck's displayed BPM ±6%. With a range set, unknown-BPM
  tracks are hidden; both boxes empty means no filter. Combines with the text filter.
- **Analyse BPM.** A checkbox in the library header, persisted. Defaults on for
  desktop and tablet, off when `App.UsePhoneLayout` is set. When on, opening a
  folder queues its tracks at priority 3.
- **Phone.** The Library tab shows Title · BPM · Time; the BPM filter row sits on its
  own line under the search box.

## 3. Deck: BPM, sync, nudge, hot cues

A new row under the transport/tempo row on every layout:

```
 128.0 BPM  [SYNC]  [‹] [›]          [1] [2] [3] [4]
```

- **BPM readout.** Displayed BPM × tempo, one decimal, live with the tempo slider;
  "—" when unknown. Tapping it opens a flyout with **×½** and **×2**, which update
  `BpmMultiplier` in the store.
- **SYNC.** One-shot: sets this deck's tempo to `otherHeardBpm ÷ thisBpm`, clamped to
  0.5–1.5. Disabled, with a tooltip saying why, when either BPM is unknown or the
  other deck has nothing loaded.
- **Nudge ‹ ›.** While held, tempo is −4% / +4%; on release it returns exactly to the
  prior value.
- **Hot cues.** Empty slots outlined, set slots filled in one of four colours. Tap
  empty: set at the playhead. Tap set: seek there, keeping the transport state
  (playing stays playing; stopped stays stopped at the cue). Long-press on touch or
  right-click with a mouse: clear. Saved to the store on every change.
- **Waveform.** `WaveformView` gains a hot-cue property and draws each set cue as a
  thin line in its colour with its number at the top.
- **Space.** The row adds ~36 px per deck. On tablet the deck playlist's max height
  drops by the same amount so the library keeps its share. The phone has room since
  its playlist is on its own tab. All three layouts are rendered and checked.

## 4. Smart auto-cue

- **Setting.** A *match tempo* combo beside *fade (s)*: **Off** (default), **Match**,
  **Match & glide back**. Persisted.
- **Look-ahead.** `DeckViewModel.PeekNext()` makes and holds the next pick (so
  shuffle's random choice happens early); `DequeueNext()` returns the held pick. If
  the playlist changes so that the held pick is gone or no longer first in an
  unshuffled queue, the hold is cleared and re-picked. Both decks' next-up tracks go
  to `AnalysisQueue` at priority 2.
- **Trigger.** For the outgoing deck, `E = LastSound ?? Duration`. Wall-clock time
  left is `(E − Position) ÷ Tempo`. The fade starts when that is ≤ the fade length,
  lasts the fade length, and stops the outgoing deck at the end, so trailing silence
  never plays. Dividing by tempo also fixes the existing bug where a sped-up track
  started its fade late.
- **Incoming start.** Hot cue 1, else `FirstSound`, else 0, seeked before `Play()`. If
  analysis is not ready, start at 0 as today; never wait.
- **Tempo match.** Only when both BPMs are known and the required change is within
  ±8%; otherwise this transition plays both at their own tempo.
  - *Match:* incoming tempo = `outgoingHeardBpm ÷ incomingBpm` before `Play()`, and
    it stays there.
  - *Match & glide back:* after the fade, incoming tempo eases linearly to 1.00× over
    8 s, driven from `Tick`. Any user tempo action on that deck (slider, SYNC, nudge,
    reset) cancels the glide.
- **Shape.** `AutoCue` stays in Audio with no Library dependency. It takes a
  `Func<Track, TrackTiming?>`, which `MainViewModel` wires to `TrackStore`.
- **Tempo echo.** Auto-cue now changes a deck's tempo, so `DeckViewModel.Refresh()`
  pulls `Tempo` back from the deck, guarded against feeding it straight back.

## Settings

`AppSettings` gains `AnalyseLibraryBpm` (bool?, null meaning "use the platform
default") and `AutoCueTempoMatch` (`Off` / `Match` / `MatchAndGlide`).

## Build order

Each step lands as its own commit(s) with its tests, and the app works after each:

1. Foundation: detectors, `TrackAnalysis`, `TrackStore`, `AnalysisQueue`.
2. Library (feature 6): rows, sort, played, recently played, BPM column and filter.
3. BPM and sync (feature 1): deck BPM readout, ×½/×2, SYNC, nudge.
4. Hot cues (feature 2).
5. Smart auto-cue (feature 3): look-ahead, trigger and start points, tempo match.
6. README: library, BPM/sync, hot cues and auto-cue sections; known limitations
   (octave folding, no phase sync yet).

## Testing

- **Detectors.** Synthetic audio generated in-test: click tracks at 90, 120, 128 and
  174 BPM (±0.5 BPM after folding); clicks under noise; leading and trailing
  silence (start/end within 50 ms); a long quiet fade-out; pure noise → null BPM;
  digital silence → null start/end.
- **TrackStore.** Round trip; corrupt file → empty; invalidation clears analysis but
  keeps hot cues and history; debounced save coalesces writes.
- **AnalysisQueue.** Priority order, de-duplication, cache skip, folder change drops
  priority 3 only.
- **Library.** Sorting by duration and BPM; 30 s played rule; recently played order
  and cap; BPM range and ≈; unknown BPM hidden under a range.
- **Deck.** SYNC maths, clamp, disabled states; nudge restores exactly; hot cue
  set/seek/clear and persistence; ×½/×2.
- **AutoCue.** Trigger on `LastSound` with tempo; start point precedence; tempo match
  inside/outside ±8% and with a BPM missing; glide reaches 1.00× and is cancelled by
  a user change; the peeked shuffle pick is the one dequeued.
- **UI smoke.** Desktop, tablet and phone renders, checked by eye. Tablet (`.touch`)
  has no render test today, so one is added.

## Out of scope

Phase (beat) sync and beat-aligned transitions — `BeatOffset` is stored for them.
Loops, EQ, a key/harmonic column, and persisting waveform peaks.
