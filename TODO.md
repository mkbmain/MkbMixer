# TODO

Open items from the load and library performance work.

## Partly fixed

### Dead file handles on pulled media
A prepared file is now test-read before `SoundFlowDeck.Load` uses it, and reopened if the read
fails. This is best effort: opening scans the whole file, so on Linux and Android the read may be
answered from the page cache even though the drive has gone. A track could then still load, play
nothing and skip with no error.

- [ ] Try it with a real USB drive: prepare the next track, pull the drive, plug it back in, then
      let that track load. Do this on Windows, Linux and Android. So far it has only been tested
      on Linux with closed and failing handles, never with real pulled media.
- [ ] If tracks still skip silently, stop treating a failed read as the end of the track, by
      recording read failures in a stream wrapper and reporting them when playback ends.

### Next track's file stays open on desktop
Decks now release files they no longer need, but the next track's file stays open by design. On
desktop that can still block a USB eject, or a rename on Windows. Only Android gets a signal that a
drive is being removed.

- [ ] Decide whether this is acceptable, or whether desktop needs a way to release it.

## Flaky tests

- [ ] `DisposeWaitsForAnEvictedOpenToo` fails rarely: once in 10 runs, then 0 in 38 with timing added.
      `Dispose` used its full 2 seconds with the open still running, when it normally returns about
      5 ms after the release, so something rare stalled for nearly 2 seconds. The cause is unknown,
      and a real stall during shutdown is not ruled out.
- [ ] `DisposeOnABlockedUiThreadWaitsOnlyForTheDecodeToStop` failed intermittently because the decode
      had not started within 1 second. It now waits 5 seconds, but it had stopped failing by then, so
      the change is unproven. Check whether the same stall is behind both failures.
- [ ] `UiSmokeTests.Renders*ToPng` and `ShortcutEditorTests.RendersEditorOverlayToPng` fail when several
      suites run at once, possibly from a shared output file (not checked).

## Rare cases left unfixed

- [ ] Same track queued twice: an auto-cue load during the wait can play it again.
- [ ] Pressing Play and then removing the next track during a file open can restart the old track.
- [ ] An exception while removing a track from the queue could escape through Play. Nothing realistic
      triggers it today.
- [ ] If the auto-cue's load throws while another load is running, that load can open its file on the
      UI thread.
- [ ] When the loaded track is also the next track, the next load opens its file on the UI thread.

## Tidying

- [ ] `DeckViewModel.PlayNextAsync` is now only called by tests, so remove it or make it internal.
