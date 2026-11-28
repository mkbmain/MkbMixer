# Performance work

Eight tasks covering the nine performance findings from the review of 2026-11-07.
No spec exists; this plan and the findings it quotes are the authority.

Baseline measurements (Ryzen 7 5700U, 8-minute 5.8 MB MP3): whole-track analysis
about 630 ms and 60.6 MB allocated; `QueueFolder` 234 ms at 5,000 tracks and 1.67 s
at 20,000; `ApplyFilter` 113 ms at 5,000 rows; decoder seek up to 5 ms; decoder open
16 ms, scaling with file size; `TrackStore.Flush` 70 ms at 20,000 tracks.

## Global Constraints

- Do NOT run `git commit`, `git add`, `git stash`, or anything else that changes the
  index, HEAD or branch. Leave all work as unstaged changes in the working tree. The
  controller checkpoints between tasks.
- Build and test with `dotnet test -c Release` from the repo root. All pre-existing
  tests must keep passing; the baseline is 401 passing. Change an existing test only
  when the behaviour it pins is deliberately changed by the task, and say so in the report.
- Tests never need a sound card. Use `FakeAudioEngine` and `FakeDeck` in `tests/Mkb.Mixer.Tests`.
- Existing behaviour stays the same unless the task says otherwise. No user-visible
  features, no refactoring outside the task.
- Comments follow the repo's style: explain why, not what.
- Nothing may block the real-time audio thread for longer than it does today, or allocate on it.
- Do not add NuGet dependencies.

## Task 1: Start playback without waiting for analysis

**Problem.** `DeckViewModel.LoadAsync` awaits `OnTrackChangedAsync`, which awaits the
whole-track analysis. So `PlayAsync` (nothing loaded, pulls the top of the playlist) and
`PlayNextAsync` (track ended) only start audio after analysis finishes. Confirmed: with a
1,500 ms analysis, playback began at about 1,500 ms. Existing tests miss it because
`FakeAudioEngine.AnalyseAsync` returns an already-completed task.

**Required.**
- `PlayAsync` and `PlayNextAsync` start audio as soon as the track has been loaded onto
  the deck, while analysis carries on in the background.
- Analysis still updates the waveform and stored info when it lands, and a newer track
  still cancels and supersedes an older analysis.
- An exception from the background analysis must not crash the process or go unobserved.
- A load failure still skips to the next queued track, and still reports through `PlaybackRefused`.
- `LoadAsync` keeps its meaning (completes once analysis has landed) if tests or callers
  rely on it; add a separate path for the play cases if that is cleanest.

**Tests.** Add to `FakeAudioEngine` a way to hold an analysis open until released. Test
that both play paths reach `PlaybackState.Playing` while analysis is still held, and that
the waveform arrives after release. Write the failing tests first and record the failure.

**Files.** `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`, `tests/Mkb.Mixer.Tests/FakeAudioEngine.cs`, a test file.

## Task 2: Fix the folder queue's quadratic cost and the store's lock hold

Two small independent changes.

**`AnalysisQueue.QueueFolder`** does a linear `_pending.Exists` per path, under the lock,
on the UI thread. Make it linear overall with a `HashSet<string>` of pending paths built
inside the method. Keep its behaviour: Library entries are replaced, an entry at NextUp
priority is not duplicated or demoted, duplicate input paths appear once. Use the same
comparison as today (ordinal). Add a test for those behaviours, and one test that 20,000
tracks queue in well under a second. Do not change `TryTake`.

**`TrackStore.Flush`** serialises all tracks while holding `_gate`, so `Get` and `Update`
wait. Copy the dictionary under `_gate`, using the same `PathComparer`, then serialise the
copy outside `_gate` but still inside `_writeGate`. `TrackInfo` values are immutable
records and `WithHotCue` clones its array, so sharing them is safe. Keep the existing
failure behaviour: `_dirty` is restored when serialising or writing fails. Add a test that
a change made during or after a flush is not lost, and that a failed serialise or write
leaves the data dirty if there is a practical way to provoke it.

**Files.** `src/Mkb.Mixer.App/Services/AnalysisQueue.cs`, `src/Mkb.Mixer.Library/TrackStore.cs`, test files.

## Task 3: Make library filtering cheap

**Problem.** `LibraryViewModel.ApplyFilter` runs on each keystroke, BPM box change and
folder load, and rebuilds `BrowserRows` with `Clear` plus one `Add` per row, each raising a
change notification into the DataGrid or ListBox.

**Required.**
- Replace the list contents with a single notification. Add a small
  `ObservableCollection<T>` subclass with a `ReplaceAll(IEnumerable<T>)` that raises one
  `Reset`, and keep `BrowserRows` the same instance so the bound control keeps its sort.
  `BrowserRows` stays usable through the normal `Add` and `Contains` used elsewhere.
- Keep `SelectedBrowserRow` if that row is still in the filtered list after a filter.
  If it has gone, clear the selection. A `Reset` must not silently lose the selection of a
  row that still matches: restore it after `ReplaceAll` if the control cleared it.
- Debounce filtering from typing only (`OnSearchTextChanged`): 150 ms after the last
  keystroke. BPM box changes and folder loads stay immediate.
- `LibraryViewModel` gains an optional constructor parameter `TimeSpan searchDebounce = default`.
  Zero means filter synchronously, so existing tests and any other constructor caller are
  unchanged. `MainViewModel`'s parameterless production constructor passes 150 ms through
  a new optional parameter on its main constructor. Create any timer on the UI thread and
  lazily, because the view models can be constructed off the UI thread in tests. A
  `DispatcherTimer` created on first use in the handler is acceptable.
- A pending debounce must not fire after `Dispose`, and `SearchAsync` must use the current
  search text even if a debounce is pending.

**Tests.** `ReplaceAll` raises exactly one notification and ends with the right contents.
Filtering with a zero debounce behaves as before. Selection survives when the row still
matches and clears when it does not. Debounce: rapid changes filter once, tested on a
headless session using `AvaloniaTest` if a timer needs a dispatcher.

**Files.** `src/Mkb.Mixer.App/ViewModels/LibraryViewModel.cs`, `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs`, a new collection class under `src/Mkb.Mixer.App/ViewModels/`, test files.

## Task 4: Reduce analysis memory

**Problem.** `TrackAnalyser` keeps `_mono` and `_peaks` at about 11 kHz plus a `ToArray`
copy: 60.6 MB for an 8-minute track. Large allocations on repeat mean more garbage
collection pauses, which suspend the managed audio callback.

**Required.** Peak memory for an 8-minute track falls to single-digit MB, ideally about
2 MB, by keeping only what the detectors and waveform need as the audio is fed in:
- Tempo: energy per hop. `TempoDetector` uses `hop = rate/100` and `window = 2*hop`, so
  `energy[f]` is the sum over hops `f` and `f+1`.
- Silence: RMS per `SilenceDetector.Window` (50 ms) window. This does not divide evenly
  into hops, so accumulate it separately.
- Waveform: one peak per hop, then bucket as `Waveform.FromSamples` does now.

Refactor `TempoDetector` and `SilenceDetector` so each has an entry point taking those
reduced inputs, with `Detect(AnalysisSignal)` delegating to it. The public `Detect`
signatures stay.

**Results must not change materially.** `DetectorsSeeTheSameSignalAsAFullDecode` and the
chunk-size tests in `TrackAnalyserTests` must still pass; BPM, offset, first and last
sound must match the `AnalysisSignal` path exactly or within the rounding of the summation
order. For the waveform, tracks shorter than 20 seconds have fewer hops than buckets. If
an existing test pins exact peaks on such a track, keep the test's intent, adjust only
what the coarser resolution forces, and explain it in the report.

Also in `SoundFlowAudioEngine.AnalyseAsync`:
- Rent the 16,384-float decode buffer from `ArrayPool<float>.Shared` and return it in a `finally`.
- Run the decode at `ThreadPriority.BelowNormal`, restoring the pool thread's priority in a
  `finally`, so analysis yields to the audio and UI threads. Wrap setting the priority so
  a platform that rejects it does not fail analysis.

**Tests.** Add a test that feeds a long synthetic signal in chunks (several minutes,
generated rather than stored) and asserts with `GC.GetAllocatedBytesForCurrentThread` that
the analyser allocates under 8 MB. Write it first and record the failure.

**Files.** `src/Mkb.Mixer.Audio/TrackAnalyser.cs`, `TempoDetector.cs`, `SilenceDetector.cs`, `SoundFlowAudioEngine.cs`, tests.

## Task 5: Stop re-decoding tracks already analysed

**Problem.** `AnalysisQueue.AnalyseForDeckAsync` decodes the whole file on every deck load
because only BPM and timing are stored, not the waveform. It then rewrites `tracks.json`
through `SetAnalysis` even though nothing changed.

**Required.** Add `WaveformCache` in `src/Mkb.Mixer.Library`:
- One file per track in a directory passed to its constructor; production uses
  `ApplicationData/MkbMixer/waveforms/`, beside `tracks.json`. The file name is the hex
  SHA-256 of the full path.
- Binary format: a magic and version, the file's size and last-write ticks (`FileStamp`),
  a peak count, then one byte per peak, quantised from 0..1 to 0..255.
- `TryLoad(path, FileStamp)` returns the `Waveform`, or null when the file is missing, its
  stamp differs, or it is corrupt or truncated. It never throws for those.
- `Save(path, FileStamp, Waveform)` writes to a temp file and moves it over, like
  `TrackStore.Flush`. A failure such as a full disk or read-only folder is swallowed: the
  cache is an optimisation.
- `Forget(path)` deletes a track's file, swallowing failures.

`AnalysisQueue` gains an optional `WaveformCache? waveforms = null` constructor parameter.
Without one, behaviour is unchanged, so tests and in-memory stores are unaffected.
- `AnalyseForDeckAsync`: on a background thread (never file I/O on the UI thread), if
  `Store.Get(path)` is analysed and the cache has a matching waveform, build the
  `TrackAnalysis` from the stored values and cached waveform and return it without decoding
  and without `Record`. No `Store.SetAnalysis`, no store write, no `Analysed` event.
  Otherwise decode as today.
- `Record` saves the waveform to the cache alongside `Store.SetAnalysis`, so background
  library analysis also fills it, using one `FileStamp` for both. Saving happens off the
  UI thread.
- The worker's skip check stays `IsAnalysed`.
- `TrackStore.ForgetMissing` needs a way to drop cached files for forgotten tracks. Make
  the smallest change that achieves it, such as returning or raising the forgotten paths,
  or having `MainViewModel.ForgetDeletedTracks` do it, and keep the existing `int` result
  or update its tests if that is cleaner.

Only `MainViewModel`'s parameterless production constructor supplies a real cache. Tests
inject their own.

**Tests.** Round trip within quantisation. Stamp mismatch, truncated file and corrupt magic
each miss without throwing. A failed save does not throw. Second `AnalyseForDeckAsync` of
an unchanged track does not call the engine (use `FakeAudioEngine.AnalysedPaths`) and does
not mark the store dirty. A changed file is decoded again. A queue without a cache behaves as before.

**Files.** New `src/Mkb.Mixer.Library/WaveformCache.cs`, `AnalysisQueue.cs`, `MainViewModel.cs`, `TrackStore.cs` if needed, tests.

## Task 6: Precompile for Android and desktop

**Overridden during execution:** the Android part below was wrong. Release builds already use AOT, so `AndroidEnableProfiledAot=false` means full AOT. The setting stays, with a comment. Only the desktop change was made.

**Problem.** `src/Mkb.Mixer.App.Android/Mkb.Mixer.App.Android.csproj` sets
`AndroidEnableProfiledAot` to false, and the commit gives no reason. Desktop archives are
self-contained but not ReadyToRun. Everything is compiled at runtime, including the code
on the audio path the first time it runs.

**Required.**
- Android: remove the `AndroidEnableProfiledAot` line, so Release builds use the default
  profiled AOT. Do not add full AOT.
- Desktop: enable `PublishReadyToRun` for publishes with a runtime identifier, in
  `Mkb.Mixer.App.Desktop.csproj`, conditioned on `RuntimeIdentifier` being set so a plain
  `dotnet build` and `dotnet run` are unaffected. CI's existing publish command must not
  need changing. Keep the existing comment in `ci.yml` accurate, adjusting it if needed.
- Measure and put in the report:
  - Android Release APK size before and after, with
    `ANDROID_HOME=/opt/android-sdk ./build-apk.sh Release`. Build "before" using a
    temporary copy of the setting, then restore it. `dist/` and build output are git-ignored.
  - Desktop publish size before and after for `linux-x64`, and time to complete
    `./Mkb.Mixer.App.Desktop --audio-info` from the published folder, run five times each
    with the median reported. Publish into a temporary directory outside the repo.
  - Confirm `dotnet publish src/Mkb.Mixer.App.Desktop -c Release -r osx-x64 --self-contained -o <temp>`
    and the same for `osx-arm64` and `win-x64` succeed from this Linux machine, or report
    what fails. CI builds these on their own runners.
- Phone startup cannot be measured here. Do not claim a startup improvement on Android.
- No APK may be left signed with anything other than the debug key, and no keystore or
  credentials may be touched.
- If the Android build cannot run here, report DONE_WITH_CONCERNS with the error rather
  than working around it. Do not install SDK components.

**Files.** The two csproj files, and `.github/workflows/ci.yml` only for the comment.

## Task 7: Stop seeks stalling the audio callback

**Problem.** `TimeStretchProvider.Seek` holds `_gate` across `_source.Seek`, which takes up
to 5 ms on a desktop and longer on phones, and `ReadBytes` takes the same lock on the audio
thread. The mixer calls both decks in one callback, so seeking one deck can stall both.
Dragging on the waveform calls `Seek` on every pointer move.

**Required.**
- `ReadBytes` must not wait for a seek. Use a try-enter on `_gate` (`Lock.TryEnter`). If
  it is held, clear the buffer and return the whole-frame length of silence, so that this
  deck is silent for that block and nothing else stalls. Do not return 0, which reads as
  end of stream. Do not move the decoder seek onto the audio thread: it would then always
  cost the callback its full duration.
- `Dispose` keeps its guarantee: once it returns, no read is touching the source. A read
  arriving during `Dispose` gets silence, and after `Dispose` gets 0 as it does today.
  Update the comments on the lock so they state the new rule.
- Check `TimeStretchTests`, including the dispose test, against the new behaviour.
- Throttle waveform drags in `WaveformView`: the press seeks immediately, moves raise
  `Seeked` at most once per 50 ms, and the release raises a final `Seeked` for the last
  position if a move was held back. Use an injectable or test-friendly clock if needed. A
  plain click must still raise exactly one seek.

**Tests.** A provider test where a source's `Seek` blocks on a test-controlled gate while
another thread calls `ReadBytes`: the read returns promptly with silence of the requested
length, and normal audio resumes after the seek completes. Throttling tests in
`WaveformViewTests`: many fast moves raise few seeks, and the last position always arrives
by release. Write the provider test first and record the failure.

**Files.** `src/Mkb.Mixer.Audio/TimeStretchProvider.cs`, `src/Mkb.Mixer.App/Controls/WaveformView.cs`, tests.

## Task 8: Open tracks ahead of time, off the UI thread

**Overridden during execution:** two prepared chains per deck, not one, because `DequeueNext` prefetches the following track before the dequeued one is loaded. Newest load request wins when loads overlap.

**Problem.** `SoundFlowDeck.Load` opens the decoder on the UI thread. Opening scans the
whole file: 16 ms for 5.8 MB here and slower on an SD card. It happens inside the auto-cue
tick and on Play.

**Required.**
- Add `Task PrepareAsync(Track track, CancellationToken ct = default)` to `IDeck`. In
  `SoundFlowDeck`, it opens the stream and builds the provider chain on a background thread
  and keeps the result in one prepared slot, keyed by track path.
- `Load(track)` uses the prepared chain if its path matches, and otherwise opens
  synchronously as today. Creating the `SoundPlayer` and adding it to the mixer stay in
  `Load`. The prepared chain carries the deck's current tempo when it is used, not the
  tempo at preparation.
- A newer `PrepareAsync` supersedes an older one. Whichever chain loses is disposed, including
  one that finishes opening after it has been superseded or after the deck is disposed.
- A preparation failure must not throw out to the caller of a fire-and-forget prefetch:
  `Load` then opens synchronously and reports the real error as it does today.
- `SoundFlowDeck.Dispose` disposes the prepared chain and waits, bounded by 2 seconds like
  `AnalysisQueue.DisposeTimeout`, for an open still running, because the engine is disposed
  right after and a decoder running against it would use freed native memory. It must not
  deadlock when called on the UI thread: no continuation may need the UI thread to finish.
- With no audio context (`_engine` null), `PrepareAsync` completes and does nothing.
- `DeckViewModel`: `PrefetchNext` also calls `_deck.PrepareAsync` for the track `PeekNext`
  returns, fire-and-forget with exceptions observed, so the auto-cue's synchronous `Load`
  and the next-track path find it ready. Call it only when the track is not null.
- `DeckViewModel` load paths awaited from the UI (`LoadAsync` and the play paths from Task 1)
  await `PrepareAsync` first, so the file is never opened on the UI thread. Task 1's
  guarantee stays: playback does not wait on analysis.
- Only the prepared slot is new state. `AutoCue` is unchanged and still calls `Load`.
- `FakeDeck` records prepared paths and completes immediately, so existing synchronous tests
  still pass. Add a way to hold a preparation open for tests.

**Tests.** Fake-deck tests: the next track is prepared when the queue changes; the play
paths prepare before load; a preparation failure leaves load working. If `SoundFlowDeck` can
run with no device, add integration tests in the style of `AudioEngineIntegrationTests`:
prepared and unprepared loads both play from position zero with the current tempo;
superseded and disposed preparations release their files, which on Linux can be checked by
a later delete succeeding or by the provider's disposal.

**Files.** `src/Mkb.Mixer.Audio/IDeck.cs`, `SoundFlowDeck.cs`, `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`, `tests/Mkb.Mixer.Tests/FakeDeck.cs`, tests.
