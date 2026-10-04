# MKB Music Mixer

A cross-platform dual-deck DJ mixer for Linux, macOS, Windows and Android tablets.

This is a rewrite of a .NET 2.0 WinForms application from roughly 2005. The original
was Windows-only by construction: it drove two `WMPLib.WindowsMediaPlayer` COM
objects, enumerated drive letters, and assumed `\` path separators throughout.
The core idea — two decks, a crossfader between them, and an auto-cue that mixes
the next track in as the current one runs out — is unchanged.

## Requirements

- .NET 10 SDK
- A working audio output device (PulseAudio, PipeWire, ALSA or JACK on Linux;
  CoreAudio on macOS; WASAPI on Windows)

## Running

```bash
dotnet run --project src/Mkb.Mixer.App.Desktop
```

## Android (tablets)

Needs the .NET Android workload (`dotnet workload install android`) and an
Android SDK. To build an APK into `dist/`:

```bash
./build-apk.sh            # Release
./build-apk.sh Debug
adb install -r dist/mkb-mixer-1.0-release.apk
```

It is signed with your local debug key unless `ANDROID_KEYSTORE`,
`ANDROID_KEY_ALIAS` and `ANDROID_KEYSTORE_PASSWORD` are set; see the top of the
script. For a quick edit-and-run loop with a tablet connected over USB:

```bash
dotnet build src/Mkb.Mixer.App.Android -t:Run
```

The app asks for access to audio files on first launch; without it the library
browser shows folders but no tracks. It browses shared storage
(`/storage/emulated/0`, starting in `Music`) by path. It is locked to landscape
because the layout needs a tablet's width; phones are not supported.

Current limitations on Android:

- **Playback stops when the app leaves the screen.** There is no foreground
  service yet.
- **SD cards and USB drives are not browsable.** Only primary shared storage is
  reachable by path; other volumes need the system folder picker.

## If playback is silent

The status bar's bottom-right corner always shows the audio route in use, for
example `♪ PulseAudio — Built-in Audio`, or `♪ no audio output` if no device could
be opened.

To see what your machine offers:

```bash
dotnet run --project src/Mkb.Mixer.App.Desktop -- --audio-info
```

That reports every backend miniaudio was compiled with, whether its context
initialises, how many playback devices it sees, and whether a device actually
opens. To force one:

```bash
dotnet run --project src/Mkb.Mixer.App.Desktop -- --backend=PulseAudio
```

Valid values on Linux are `PulseAudio`, `Jack`, `Alsa` and `Oss`. A specific sink
can be forced by name substring:

```bash
dotnet run --project src/Mkb.Mixer.App.Desktop -- --device="Built-in Audio"
```

### "only a dummy/null sink is available"

The backend connected but offered nothing except a null device, which discards
everything. The app refuses to use one, because playing into a null sink is silent
and races the playhead to the end of the track. Check that a sound server is
actually running and exposing a sink:

```bash
pactl info                 # PulseAudio / pipewire-pulse server
pactl list short sinks     # should list at least one real sink
wpctl status               # if you are on PipeWire
aplay -l                   # ALSA hardware the kernel can see
```

The app probes each backend, then the default device followed by each named
device, against several formats, and uses the first combination that opens.
`Default Device` failing while a named device works is common and handled
automatically.

ALSA errors such as `unable to open slave` or `Unknown PCM dmix` usually mean
PulseAudio or PipeWire already holds the sound card, so the raw ALSA device cannot
be opened. The app tries PulseAudio first and falls back to JACK then ALSA, each
backend probed independently, because a backend whose *context* initialises can
still fail to open a *device*.

## Tests

```bash
dotnet test
```

The suite covers the crossfade curve, the auto-cue state machine, playlist and
settings persistence, waveform reduction, and a headless render of the real window.
None of it needs a sound card.

## Layout

| Project | Contains |
|---|---|
| `src/Mkb.Mixer.Audio` | `IDeck`/`IAudioEngine`, the crossfade curve, the auto-cue state machine, waveform reduction, and the SoundFlow implementation |
| `src/Mkb.Mixer.Library` | Folder scanning, ID3 tag reading, M3U playlists, settings |
| `src/Mkb.Mixer.App` | Avalonia UI (MVVM), shared by every platform |
| `src/Mkb.Mixer.App.Desktop` | Desktop entry point and the `--audio-info` diagnostic |
| `src/Mkb.Mixer.App.Android` | Android entry point, permissions and manifest |
| `tests/Mkb.Mixer.Tests` | xUnit |

Everything above `Mkb.Mixer.Audio` talks to `IDeck` and `IAudioEngine`, so the
audio library is swappable and the logic is testable without an audio device.

## What changed from the original

**Fixed bugs**

- *The crossfader was discontinuous.* Deck A sat at full gain across the whole
  left half of the fader, then jumped from 1.0 to 0.49 at the midpoint. The
  `(100 - v) * 2` term also overflowed to 200 and was silently clamped. It is now
  a constant-power curve, continuous and symmetric, verified by tests.
- *The auto-cue often never fired.* The trigger was `if (timeleft == 20)` — exact
  integer equality against a value sampled by a 1050 ms timer. A tick landing on
  21 then 19 skipped the window entirely. It is now a `>=` threshold guarded by an
  explicit state machine, with the fade interpolated on elapsed wall-clock time.
- *Scanning froze the UI.* The original recursed on the UI thread calling
  `Application.DoEvents()`. Scanning is now async and cancellable.
- *Closing the app called `Process.GetCurrentProcess().Kill()`.* The engine is
  disposed properly instead.
- Roughly thirty empty `catch {}` blocks are gone; failures that matter surface in
  the status bar.

**Added**

- Waveform display with click-and-drag seeking. The original had no seek at all.
- ID3/Vorbis/MP4 tags, so lists show `Artist — Title` rather than `C:\Music\x.mp3`.
- Playlists saved as M3U, and settings persisted between runs.
- Tempo control that preserves pitch (WSOLA) rather than resampling.
- FLAC, OGG, M4A, AAC and Opus support.

## Known limitations

- **WMA only decodes on Windows.** It is a Microsoft codec with no cross-platform
  decoder in this stack. The original supported `.mp3`, `.wma` and `.wav`.
- **Tempo always preserves pitch.** SoundFlow routes `PlaybackSpeed` through WSOLA
  unconditionally, so the original's pitch-shifting speed control cannot be
  reproduced without adding a resampling path.
