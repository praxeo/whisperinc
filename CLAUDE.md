# WhisperInk — CLAUDE.md

Push-to-talk dictation for Windows (WPF, C#/.NET 8). Hold **Ctrl+Space**, talk, release, and the transcript is pasted where you were typing. Cloud and local (CrispASR) transcription sit behind one interface. It's built for clinical dictation into an EHR.

**Read [Part 1](#part-1--start-here) every session.** It has the current state, the rules that don't bend, how to build, test and deploy, and where to look when something breaks. The rest is reference: go to the Part for the area you're changing.

| Part | Covers |
|---|---|
| [1. Start here](#part-1--start-here) | What it is, current state, the rules, build/test/deploy, triage, working with the owner |
| [2. How a dictation works](#part-2--how-a-dictation-works) | Press → release → outcome, threads, timing |
| [3. Reference](#part-3--reference) | Files, config, hook, capture, reliability, cloud connections, delivery, UI, logging, dispatch |
| [4. Providers](#part-4--providers) | Catalog, which to use, biasing with measured results, per-provider notes |
| [5. Local ASR (CrispASR)](#part-5--local-asr-crispasr) | Deployment, updating, how it's run, DLLs, punctuation, building from source |
| [6. Recipes](#part-6--recipes) | Adding a provider, field, knob or menu item; changing a default; touching the stop path; measuring |
| [7. Testing](#part-7--testing) | The harness, the live probes, and what no test covers |
| [8. Troubleshooting](#part-8--troubleshooting-and-gotchas) | Gotchas, by area |
| [9. History and decisions](#part-9--history-and-decisions) | Timeline, and what was decided against |
| [10. Roadmap](#part-10--roadmap-and-open-questions) | Open questions, known bugs, backlog |
| [Maintaining this file](#maintaining-this-file) | How to keep it true |

---

# Part 1 — Start here

## 1.1 What this is, and the bar it has to clear

WhisperInk is a push-to-talk dictation tool for Windows: hold **Ctrl+Space**, talk, release, and the transcript is pasted into whatever window you were typing in. It is a WPF app (C#, .NET 8) with a pluggable transcription layer: cloud APIs (ElevenLabs, Deepgram, Soniox, Google, …) and local models run through CrispASR on the machine's GPU or CPU.

Its main use is **clinical dictation**: exam findings and notes pasted straight into an EHR. That sets the engineering bar. A wrong word can be a wrong finding, a lost dictation is lost clinical work, and a paste into the wrong window can land in the wrong chart. Every design decision below follows from that. When in doubt, choose the behaviour that loses nothing and says so out loud.

## 1.2 Current state (2026-09-23)

| | |
|---|---|
| `main` | Pushed to `origin` (`praxeo/whisperinc`, **public**). The last app-code commit is the quiet-speech fix (`SpeechDetector`), the commit right after `8c0a52d` |
| Running build (desktop) | `_publish\WhisperInk.exe`, a self-contained single-file publish of that commit. Old test builds `%USERPROFILE%\WhisperInk-step0\` and `-step1\` are stale; launching one alongside `_publish` gives two apps answering Ctrl+Space |
| Active provider (desktop) | `elevenlabs-medical` (Scribe v2 Medical), with the upload streamed while you talk. **Read `config.json` → `ActiveProviderId` rather than trusting this line**; it has changed often |
| Vocabulary | 21 terms in the shared Context Bias list and 228 Scribe-only keyterms, so 249 go to ElevenLabs on every take. Over 100, ElevenLabs bills each take as at least 20 s |
| Local ASR | CrispASR **v0.8.30** CUDA (prebuilt release) in `%APPDATA%\.WhisperInk\cohere-gguf\`. v0.8.36 is out, not deployed. Best local preset: `qwen3-asr-1.7b-local` |
| Machines | Desktop: 2× RTX 3090 + RTX 3080, CUDA. Laptop: 8-core Ryzen 5825U on CPU, still a pre-v0.7 CrispASR. `config.json` is per machine |
| Open work | [Part 10](#part-10--roadmap-and-open-questions) |

## 1.3 The rules that don't bend

1. **Never lose a dictation.** Every take that passes the guards is journaled to `%APPDATA%\.WhisperInk\unsent\` (the WAV and a sidecar, queued before transcription starts). It is deleted only once its text is delivered, and kept with a reason on every other outcome, including a crash (see [3.5](#35-reliability-never-lose-a-dictation)). The newest few takes the silence guard drops are kept too, in case one was quiet speech. A change to the stop path that can drop a take is a bug, even on an error path.
2. **Never paste into the wrong place.** Text is pasted only when the window the take *started* in is verifiably in front. Otherwise it goes to the clipboard with a warning. Log window handles, never titles, because titles can carry patient names.
3. **Fail loudly.** Every failure gets the Error tone, a status and a `debug.log` line. "Delivered, but check it" gets the Warn tone. `Success` means *all of it, pasted where you were typing* and nothing less. A silent failure, such as a backend returning empty text or a mic that quietly stopped, has been the worst bug class in this codebase.
4. **Nothing slow on the hotkey path, or on the UI thread at all.**
   - The keyboard-hook callback runs **on the UI thread**, so a UI-thread stall delays every keystroke on the machine.
   - The press path opens no device and does no network work there.
   - The stop path holds the recording lock (`_recState` = Stopping) the whole time, so anything slow there is a dead hotkey.
   - Transient status text uses `FlashStatus`, never a `Task.Delay` inside the stop path.
5. **Measure before believing.** Vendor docs have been wrong here repeatedly: Smallest.ai's language list, Reson8's parameters, ElevenLabs' `file_format` latency claim, and upstream CrispASR "no-op" biasing claims that quietly stopped being true. Numbers in this file carry a date and a method. Re-measure before relying on an old one, and A/B any CrispASR release before deploying it.
6. **The repo is public; the audio is clinical.** Never commit API keys, voice recordings, transcripts or personal names. `.gitignore` covers `_scratch/**/*.wav` and `_scratch/**/results/`. `debug.log`, `history.json`, `unsent\` and support bundles do contain transcripts, which is a known gap in [Part 10](#part-10--roadmap-and-open-questions).

## 1.4 Build, test, deploy

```powershell
# build (0 warnings expected)
dotnet build -c Release

# harness: pure logic, fake servers, streamed upload (no API calls, no crispasr)
cd _scratch\crisp-harness
.\make-speech.ps1                 # once per machine: speech.wav is git-ignored, and every run reads it, even `fast`
dotnet run -c Release -- fast
# the full run adds the real crispasr.exe on CPU:  dotnet run -c Release
```

**Deploying to the desktop's running copy.** `publish.ps1` deletes `_publish\` and republishes, and it can't while WhisperInk runs, because the exe is locked. The procedure used on 2026-09-23 costs about 6 s of downtime and never interrupts a take:

1. **Commit first**, then publish. The version stamp (About box, support bundle) is git HEAD at publish time, so a build from uncommitted changes carries the previous commit's hash: the `a9e288a` deploy was stamped `a16352c`. Publish to a staging folder outside the repo with the same flags `publish.ps1` uses:
   `dotnet publish WhisperInk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=true -o <stage>`
2. Check `debug.log` for a take in flight. The last `StartBatchDictation: capturing` line must be followed by a line that ends the take:
   - `Batch pipeline:`;
   - `StopBatchDictation: exit` or `UNHANDLED`;
   - an early discard: a `[skip]` for a tap, no audio or silence, or an `[error]` saying nothing was recorded or there's nothing to transcribe.
   
   Two lines **don't** end a take: `[skip] hotkey pressed while the previous take is still transcribing`, and a microphone `[error]` that says "mid-dictation". Until the take has ended, wait. The owner may be dictating.
3. Copy `_publish\WhisperInk.exe` somewhere as a rollback, then `Stop-Process` WhisperInk. A kill is safe: a take still being recorded is the only thing lost, and step 2 rules that out. Anything journaled is recovered at the next start.
4. Copy the staged files over `_publish\`. **Retry on "file in use"**: OneDrive or Defender can hold the exe for a second or two after the process exits.
5. Relaunch through `explorer.exe "<repo>\_publish\WhisperInk.exe"`. A process started directly from a tool's shell can be killed along with that shell.
6. Read the new `debug.log` header: the `Active provider: …` line, and any `Added new default provider` / `Repaired …` lines.

`LoadConfig` does not save after merging new defaults. A new preset lives in memory, and is re-added and re-keyed on each launch, until something calls `SaveConfig` (a provider switch, the settings dialog, a menu toggle). That's harmless, but it's why those startup lines repeat.

**Git.** Work happens on `feat/…` or `fix/…` branches, which are fast-forwarded into `main` and pushed. Commit or push only when the owner asks. Commit subjects follow `feat:` / `fix:` / `docs:` / `refactor:` / `perf:` / `test:`, with a body saying what changed and why, including measured numbers, and ending with the `Co-Authored-By:` line. Working copies are **CRLF** and the repo stores LF: `core.autocrlf=true` converts both ways, and there's no `.gitattributes`. The Edit tool preserves line endings; `sed -i` and ad-hoc scripts can write LF lines into CRLF files, so prefer the Edit tool.

## 1.5 When something goes wrong

Start in `%APPDATA%\.WhisperInk\`:

| Look at | For |
|---|---|
| `debug.log` | This session. Time-only timestamps; the header line has the date. Prefixes are listed in [3.9](#39-logging) |
| `debug.previous.log` | The session before (a restart rotates it). **Grab it before a second restart overwrites it** |
| ↻ **Unsent dictations** (tray or bar menu) | Every take that wasn't delivered, with its reason. Retry puts the text on the clipboard |
| History window | Every take that *was* delivered, including those only put on the clipboard |
| Support bundle (tray) | A zip of the config summary and log tails for sharing. It contains transcript text |

| Symptom | First place to look |
|---|---|
| Nothing happens on Ctrl+Space | `[hook-watchdog]` lines. Another app may own Ctrl+Space (PowerToys Peek did on 2026-08-28), or two WhisperInk copies are running |
| Every take fails with 401 | The `Active provider: … (auth=…)` line. The key is sent in the wrong header, or it's missing |
| A take vanishes with a quiet blip or "Too quiet?" | The silence gate judged it silent. It's under ↻ Unsent → 🔇 Judged silent; compare the `[skip]` line's RMS and speech values with [3.4](#34-capture-pipeline-miccapturecs) |
| A take is "lost" | ↻ Unsent first, then History, then the log around its time. `[unsent] kept …` gives the reason, with the transcriber's `HTTP` or error line just before it. A failed request logs no `[error]` |
| A local model returns empty text | `CrispAsr(` lines. Exit code `-1073741515` means a missing DLL |
| A local model transcribes badly | Which GGUF `LocalModelGlob` actually picked up (see [6.1](#61-add-a-provider)) |
| Slow cloud takes | `[net] new connection` (the connection wasn't reused) and `took …ms` lines |

## 1.6 Working with the owner

- The owner is a clinician who **dictates to Claude with WhisperInk itself**. Messages can carry ASR artifacts ("Scribe VT" meant Scribe V2, "11 labs" meant ElevenLabs), and a test dictation can land in the chat. Read for intent.
- They want evidence and a recommendation, not a survey of options. Measured numbers beat claims.
- Product decisions already made, so don't re-propose them without new evidence ([Part 9](#part-9--history-and-decisions)):
  - no LLM post-processing or "correction" of transcripts;
  - no realtime/partials mode for now;
  - Modulate was rejected;
  - Scribe Realtime is unfit;
  - local CPU inference on the laptop is acceptable, so don't push cloud because it's faster.
- `plans/` holds past sprint prompts and handoffs, and `LEARNINGS.md` holds build gotchas. Neither is kept current; this file is.

---

# Part 2 — How a dictation works

Everything below is in `MainWindow.xaml.cs` unless another file is named. Read this before changing the press or stop path.

## 2.1 Press

`KeyboardHookService` sees Ctrl (either side) held and then Space go down. It swallows the Space, turns on key suppression at once, and queues `StartBatchDictation(target)` with `Dispatcher.BeginInvoke`, where `target` is the foreground window at that moment. The hook callback itself runs on the UI thread, which installed it, and only queues work, so it returns fast. Space pressed before Ctrl does nothing.

1. **Key check.** A provider that needs a key and has none shows "No API key!" and nothing is recorded. That path has no tone and no log line, and it may leave Ctrl logically held ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)). "Needs a key" means anything except a CrispASR preset or a *localhost* `Http` provider.
2. **Recording lock.** `_recState` goes 0→1 by `Interlocked.CompareExchange`. If a previous take or a retry is still transcribing (state 2), the press gets the **Dismissed** blip and a `[skip]` line. It used to be ignored silently.
3. **Target.** `_pressTicks` and `_targetWindow` are recorded now, when the take really starts. A press during a previous take's transcription used to re-point that take's paste.
4. **Start chirp, first.** It's the only confirmation that the press registered, so nothing queues ahead of it (enqueue cost ~0 ms, `UiSoundPlayer`).
5. **Housekeeping.** `BeginSuppression` re-arms the suppression the hook turned on at Space-down, modifiers are released (`TextInjector.ReleaseAllModifierKeys`), the mic idle timer stops, and the bar turns red with "🎙 REC" and the histogram.
6. **Streamed upload.** For an ElevenLabs provider with `StreamUpload` on, `BeginStreamedTake` opens the take's request now; its network work runs on the thread pool ([3.6](#36-cloud-connections-and-the-streamed-upload)).
7. **Capture.** `MicCapture.BeginCapture(sink)` attaches a WAV writer and seeds it with the last 400 ms from the pre-roll ring, so the recording starts *before* the press. If there's a stream, the sink gets the same bytes. On a cold device it opens one (~130 ms) and the pre-roll is 0. If no device opens: "No mic!" and the Error tone.
8. **Pre-warm.** With no stream, `PrewarmConnection` opens the connection to a cloud provider while the user talks.

## 2.2 Release

While recording, the hook swallows every physical Ctrl and Space event. The first key-up of either ends the take by queueing `StopBatchDictationAsync`, which runs on the UI thread through `RunSafe` so faults reach `debug.log`. `_recState` goes 1→2 and stays there until the `finally`.

1. **Capture ends.** `EndCapture(postRollMs)` runs on the thread pool. It waits up to 80 ms for the in-flight buffer so the last word isn't clipped, then returns the WAV. A tap skips the wait.
2. **Guards.** Anything that isn't a tap first gets the **Stop** chirp (800 Hz). Then each guard can end the take early:

   | Guard | Condition | Result |
   |---|---|---|
   | Tap | Held < `MinHoldMs` (250) | Dismissed blip, "(tap)" — no API call |
   | No audio | Empty WAV | Dismissed, or Error "Mic lost!" if the device died |
   | Mic cut out | Device error mid-take, or capture ≥ 1 s shorter than the hold (a stall; the device is reopened) | Flagged; the take continues and is delivered with a warning |
   | Dead input | Peak exactly 0: privacy switch or hardware mute | Error "No mic signal!" |
   | Silence | RMS < `SilenceThreshold` (0.003) **and** no sustained speech (`SpeechDetector`) | Dismissed "(silence)", or Warn "Too quiet? Saved ↻" after a hold of 1.5 s or more. The take is kept under ↻ Unsent → 🔇 Judged silent. Error "Mic lost!" if the mic cut out (kept the same way). The levels are logged in the `[skip]` line |

   The debug WAV (`Documents\MyRecordings\temp_audio.wav`) is written fire-and-forget *before* the silence gate, so a misjudged clip can be checked.
3. **Journal.** Only for takes that passed every guard (the silence guard keeps its own separately). `UnsentTakes.Begin` queues the WAV and sidecar write on the thread pool and returns at once. The transcription never waits for it, and a streamed take has been uploading since the press. From here on, every outcome either deletes the take or keeps it with a reason.
4. **Transcribe.** `TranscribeTakeAsync` calls the provider's transcriber from the factory under `TranscriptionDeadline.For(provider, seconds)`. A streamed take is finished, or falls back to the WAV upload ([3.6](#36-cloud-connections-and-the-streamed-upload)). It logs `{provider} took Nms … on Mms audio`.
5. **Coverage.** For a provider that reports word timing (ElevenLabs), `TranscriptCoverage` checks whether the text stops well short of the last speech in the WAV.
6. **Deliver.** `DeliverAsync` pastes only if `_targetWindow` is in front, allowing about 150 ms for a granted switch to land. Otherwise the text goes to the clipboard.
7. **Record.** The text goes to History and the journal entry is deleted, or kept as `incomplete`.
8. **Cue and log.** The outcome's sound (table below), then `Batch pipeline: capture=… transcribe=… paste=… TOTAL=…`.
9. **`finally`, always.** Cancel an unfinished stream, release modifiers (the hook swallowed the physical key-up, so a stuck Ctrl would turn the next keystroke into Ctrl+key), reset `_recState` to 0 and the UI, and restart the mic idle timer.

## 2.3 Outcomes and what the user hears

| Outcome | Tone | Status flash | Also |
|---|---|---|---|
| Pasted, complete | **Success** (1600 Hz) | — | Journal entry deleted |
| Pasted, but the transcript may stop early | **Warn** (660 Hz ×2) | ⚠ Check the end! | Balloon; take kept as `incomplete` |
| Pasted, but the mic cut out mid-take | Warn | ⚠ Mic cut out! | Balloon |
| Not pasted (window no longer in front) | Warn | Copied — paste it | Text on the clipboard; balloon |
| Tap | **Dismissed** (520 Hz, quiet) | (tap) | — |
| Judged silent | Dismissed; **Warn** after a hold of 1.5 s or more | (silence); after a long hold, Too quiet? Saved ↻ | Kept under ↻ Unsent → 🔇 Judged silent (the newest 5). "Mic lost!" (Error) instead if the mic cut out; kept the same way |
| No text for a take with sound in it, even quiet speech | **Error** (300 Hz) | No text! Saved ↻ | Take kept; `[error] … returned NO TEXT` |
| Deadline or request failed | Error | Timed out / Failed — saved ↻ | Take kept; balloon. Soniox, Deepgram, Chirp 3, Modulate, Smallest and Reson8 also land here on an *empty* transcript, instead of "No text!" ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)) |
| No API key | — (no tone) | No API key! | Nothing recorded, nothing logged |
| Retry from ↻ (menu) | Success; Warn for a local fallback or a possibly incomplete result; Error on failure or missing audio | Recovered — copied / Fallback — copied / ⚠ Copied — check end / Retry failed / Audio missing! / Busy — try again (no tone) | Text goes to the **clipboard**, never pasted, with a balloon. A failure keeps the take as "retry failed: …" |
| Mic died, dead input, no device | Error | Mic lost! / No mic signal! / No mic! | "🎙 MIC LOST" immediately if it dies mid-take |
| Exception in the stop path | — (the exception is logged) | — | Take kept as `failed` (`WhisperInk error (…)`) |

## 2.4 Threads

| Thread | Runs |
|---|---|
| UI (dispatcher) | **The keyboard-hook callback.** A low-level hook calls back on the thread that installed it, and code comments calling it a separate "hook thread" are wrong. It only queues work, so it returns within `LowLevelHooksTimeout`. Also: start/stop orchestration and the stop path's `await` continuations (it never uses `ConfigureAwait(false)`; only two connection helpers do), delivery, retries, menus, config, every `DispatcherTimer` |
| NAudio capture thread | `MicCapture.OnDataAvailable` and `OnRecordingStopped` under `_gate`: the WAV writer, the pre-roll ring, and the stream sink (`StreamedTranscription.Append`, which only copies and queues). A lost mic is reported back with `BeginInvoke` |
| Thread pool | `EndCapture` (blocks up to `PostRollMs`), the silence measurement (`SpeechDetector.Measure`), UI sounds, the debug WAV, journal writes and deletes, the health loop, the GPU probe, the pre-warm, the streamed upload's `SendAsync` (in `Task.Run`, so first-use proxy detection can't block the UI), transcriber internals, and CrispASR stdout/stderr handlers |
| Dedicated STA threads | Every clipboard operation, joined synchronously (`TextInjector`) |

`_recState` changes only through `Interlocked.CompareExchange` (idle → recording at the press, recording → stopping at release, idle → stopping for a retry) and is reset with `Volatile.Write`. `Log()` appends with no lock, so two threads logging at once can occasionally drop a line.

## 2.5 Timing (desktop, 2026-09-23, Scribe Medical)

A typical 3–10 s take: capture 5–45 ms (the post-roll wait), transcription ~250–500 ms from release (streamed), paste 25–60 ms. That's roughly 0.3–0.6 s from key-up to text. The first take after a restart is slower (process warm-up; one took 804 ms). The first take after the mic has been idle 180 s starts cold (no pre-roll).

---

# Part 3 — Reference

## 3.1 Where everything lives

**Three places:**

1. **This repo**: `OneDrive\Desktop\whisperinc\` on the desktop. Resolve paths relative to the repo; never hardcode them.
2. **`..\CrispASR\`**: an optional sibling clone of the ASR engine, for source builds only, with one uncommitted local patch ([5.6](#56-building-from-source-rarely-needed)).
3. **`%APPDATA%\.WhisperInk\`**: all runtime state. It is not OneDrive-synced, which matters because it holds clinical audio and text.

| In `%APPDATA%\.WhisperInk\` | What |
|---|---|
| `config.json` | All settings and providers, including API keys. The app **rewrites it from memory** on every save |
| `config.json.bak-*` | Hand-made backups taken before edits |
| `debug.log`, `debug.previous.log` | This session's log and the one before. They contain transcript text |
| `history.json` | The last 100 delivered transcripts, newest first, **in plain text** (`{Timestamp, Text, TimeStr}`) |
| `unsent\` | `take-yyyyMMdd-HHmmss-fff.wav` plus a `.json` sidecar for every undelivered take |
| `cohere-gguf\` | `crispasr.exe`, its DLLs, every `*.gguf` model, and `.old-*` backups from CrispASR updates |
| `google-chirp3-sa.json` | The Chirp 3 service-account credential; `config.json` points to it |

**Two more places outside it:**
- The debug copy of the last take, `Documents\MyRecordings\temp_audio.wav`. It **is** OneDrive-synced on the desktop.
- Support bundles, which land on the **Desktop**, also synced.

**Source files** (line counts as of 2026-09-23):

| File | Lines | Responsibility |
|---|---|---|
| `MainWindow.xaml(.cs)` | 79 / 2234 | The floating bar and all orchestration: press/stop paths, dispatch, delivery, retries, config load/save, `BuildAppMenu`, tray and health wiring, the shared cloud `HttpClient`, pre-warm and stream hand-off |
| `App.xaml(.cs)` | 9 / 63 | Startup, plus three crash handlers (UI-thread, AppDomain and unobserved-task) that write `Exception.ToString()` to `debug.log`. UI-thread exceptions are marked handled, so the app keeps running |
| `AppConfig.cs` | 1084 | The `TranscriberKind` enum, the `ApiProvider` model (settable fields, `Resolved*` helpers, `InheritFromSibling`, `RepairSupersededDefault`), `CreateDefaults()` and `InferKindFromLegacyId`. Its `AppConfig` class is **never instantiated**: config is read by hand in `LoadConfig` and written as an anonymous object |
| `KeyboardHookService.cs` | 251 | The `WH_KEYBOARD_LL` hook: the Ctrl+Space state machine, suppression, the synthetic-event filter, the watchdog |
| `MicCapture.cs` | 383 | The warm mic, `PreRollRing`, the stream sink, mic-failure flags |
| `SpeechDetector.cs` | 135 | The silence gate's measurement: peak, RMS, the take's own noise floor, and sustained speech in 30 ms frames ([3.4](#34-capture-pipeline-miccapturecs)) |
| `UiSoundPlayer.cs` | 229 | The six synthesized tones on one persistent output, following the default device |
| `TextInjector.cs` | 282 | Paste with clipboard restore, `CopyToClipboard`, `ReleaseAllModifierKeys`. `TypeTextTo` and `GetSelectedText` are **dead code** left over from the removed realtime mode |
| `ITranscriber.cs` | 64 | `ITranscriber` (`IsReady`, `TranscribeAsync(wav, biasTerms, ct)`) and `ITranscriptCoverage` (word timing) |
| `TranscriberFactory.cs` | 91 | Caches one transcriber per provider id; `Drop(id)` and `DropAll()` |
| `HttpTranscriber.cs` | 315 | OpenAI-style multipart (Mistral, OpenAI, Cohere v2, ElevenLabs, user-run local servers), the ElevenLabs extras and cleanup, the only `ITranscriptCoverage`, and `BeginStreamedTranscription` |
| `StreamedTranscription.cs` | 263 | The ElevenLabs upload streamed from key-press ([3.6](#36-cloud-connections-and-the-streamed-upload)) |
| `CrispAsrServerTranscriber.cs` | 539 | Spawns and supervises one `crispasr.exe --server` per local preset ([5.3](#53-how-whisperink-runs-it-crispasrservertranscriber)) |
| `DeepgramTranscriber.cs`, `SonioxTranscriber.cs`, `GoogleChirp3Transcriber.cs`, `ModulateTranscriber.cs`, `SmallestTranscriber.cs`, `Reson8Transcriber.cs` | 214–443 | The protocol-specific cloud clients ([Part 4](#part-4--providers)) |
| `TranscriptionDeadline.cs` | 72 | Per-take deadlines, and `HttpBackstop` |
| `TranscriptCoverage.cs` | 115 | The incomplete-transcript check |
| `UnsentTakes.cs` | 298 | The take journal: begin, deliver, keep, recover, prune, plus the takes judged silent |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | 99 / 66 / 30 | Delivered-transcript history and its viewer |
| `HealthProbe.cs`, `ProviderDiagnostics.cs` | 214 / 140 | The 60 s background health check (the dot and banner), and the on-demand "Diagnose" report |
| `MenuModel.cs`, `TrayIcon.cs` | 83 / 138 | The shared `MenuNode` tree with its WPF renderer, and the tray icon with its WinForms renderer and balloons |
| `ProviderSettingsWindow.xaml(.cs)` | 288 / 283 | Edit providers, the active provider and the GPU backend. It edits a clone (`CloneProvider`) |
| `ContextBiasWindow.xaml(.cs)` | 55 / 39 | The shared bias-list editor, one term per line |
| `SupportBundle.cs` | 263 | Zips the diagnostics to the Desktop ([3.8](#38-ui-menus-health-history-bundle)) |
| `CrispGpuProbe.cs` | 154 | GPU detection for the menu tooltip. It runs `vulkaninfo --summary`, then `nvidia-smi`; its doc comment, which says `crispasr --help`, is stale |
| `AutoStart.cs` | 43 | The `HKCU\…\Run\WhisperInk` value |
| `AboutWindow.xaml(.cs)` | 55 / 37 | Version, commit, build date, runtime, OS, README link |

**Other files in the repo:**

| Path | What it is | Current? |
|---|---|---|
| `publish.ps1`, `publish-framework-dependent.ps1` | Single-file win-x64 publish to `_publish\` (self-contained, ReadyToRun) or `_publish-fd\` | Yes |
| `scripts\install.ps1 [-Desktop]` | `publish.ps1`, then Start-menu (and Desktop) shortcuts | Yes |
| `scripts\install-shortcuts.ps1`, `scripts\uninstall.ps1 [-RemoveBinaries]` | Shortcuts; removal of shortcuts and the Run key. `%APPDATA%` is never touched | Yes |
| `scripts\update-crispasr.ps1 [-Tag] [-Asset]` | Deploys a prebuilt CrispASR release ([5.2](#52-updating-prebuilt-releases-the-normal-path)). **Always pass `-Tag`**: the default is v0.7.1 | Yes |
| `scripts\build-crispasr.ps1` | Source build ([5.6](#56-building-from-source-rarely-needed)) | Risky; see there |
| `scripts\download-cohere-{gguf,q4,q6k}.ps1` | Hugging Face downloads into `cohere-gguf\` | q4 and q5 would hijack `cohere-gguf-server`'s loose glob |
| `scripts\generate-icon.ps1` | Regenerates `Assets\icon.ico` | Yes |
| `_scratch\…` | Test harnesses and measurement tools ([Part 7](#part-7--testing)) | Yes |
| `README.md` | User-facing setup and features | **Stale** in many places; see [Part 10](#part-10--roadmap-and-open-questions) |
| `docs\TRANSCRIPTION_ACCURACY_GUIDE.md` | User-facing accuracy guide | Lags: no Scribe Medical, no streaming |
| `LEARNINGS.md` | Build and packaging lessons (April 2026) | Still valid; folded into [8.5](#85-building-and-tooling) |
| `plans\*.md` | Past sprint prompts and handoffs (elevenlabs-web port, Cohere biasing, packaging, accuracy plan) | Historical |
| `server\` | The April FastAPI Cohere server | Historical, unused |

## 3.2 Configuration

`config.json` is read by hand in `MainWindow.LoadConfig` (`JsonDocument`, one try/catch around everything) and written by `SaveConfig`, whole-file, with enums as strings.

**Root keys.** All 18 are read and written.

| Key | Default | Notes |
|---|---|---|
| `ActiveProviderId` | `mistral` | Unvalidated; an unknown id falls back to the first provider |
| `Providers` | — | Missing or empty → `CreateDefaults()` |
| `ContextBiasTerms` | `[]` | The shared vocabulary; blank entries are dropped |
| `IsSoundEnabled` | true | |
| `SelectedDevice` | 0 | A mic **index**. Indexes shift when devices come and go |
| `WarmMicEnabled` | true | "⚡ Instant start". Turning it OFF is buggy ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)) |
| `WarmMicIdleSeconds` | 180 | `Max(0)`; 0 = never release the mic |
| `PreRollMs` | 400 | Clamped 0–3000; takes effect the next time the device opens |
| `PostRollMs` | 80 | Clamped 0–1000 |
| `MinHoldMs` | 250 | Clamped 0–2000 |
| `SilenceThreshold` | 0.003 | Whole-take RMS under which a take *may* be silent: it's dropped only if `SpeechDetector` also finds no sustained speech. Clamped 0–0.5; 0 disables the gate |
| `ClipboardRestoreMs` | 1000 | How long after a paste the old clipboard is put back ([3.7](#37-delivery-textinjectorcs-mainwindowdeliverasync)). Clamped 250–10000; read only if it's a JSON number |
| `StreamUpload` | true | Read only if it's a JSON bool |
| `CrispGpuBackend` | `auto` | Normalized to auto/cpu/vulkan/cuda/metal. The menu doesn't offer metal |
| `QuitOnClose` | false | |
| `LaunchAtStartup` | false | The registry value wins at startup |
| `HasSeenFirstRun` | false | |
| `MistralApiKey` | — | Legacy. Only seeds `mistral` when there are no providers |

**Provider fields** (`ApiProvider`: 29 settable fields, every one parsed in `LoadConfig` and copied in `CloneProvider`):

| Group | Fields |
|---|---|
| Identity and HTTP | `Id`, `Name`, `BaseUrl`, `ApiKey`, `TranscriptionEndpoint`, `AuthHeaderName` (blank = Bearer), `ModelFieldName` (blank = `model`), `TranscriptionModel`, `Language` (default `en`; `auto` = let the provider detect), `TranscriptionTemperature` (nullable), `SupportsTranscription` (**read, saved and shown, but used by nothing**) |
| Dispatch and bias | `TranscriberKind`, `BiasMechanism` (baked per preset; the dialog shows it read-only; only `HttpTranscriber` consults it), `ContextBiasMode` (legacy fallback), `HotwordsBoost` (Parakeet trie; null = off) |
| ElevenLabs | `ScribeKeytermsRaw` (newline-separated), `TagAudioEvents` (false), `NoVerbatim` (true) |
| Local CrispASR | `LocalServerPort`, `LocalModelGlob`, `LocalBackendHint`, `LocalGpuBackend` (blank = global), `LocalModelFolder` (blank = `cohere-gguf`), `LocalBeamSize` (null = greedy on v0.8.30), `LocalPuncModel`, `LocalTruecaseModel`, `LocalExtraParams` |
| Passthroughs | `DeepgramExtraParams`, `Reson8ExtraParams`, and `LocalExtraParams` above. **String values only**: numbers and bools are silently dropped, so quote them |

**Computed properties** (no setters): `ResolvedTranscriptionUrl`, `ResolvedModelField`, `ResolvedAuthHeaderName`, `UsesCustomAuthHeader`, `IsElevenLabs`, `ResolvedBiasMechanism`, `IsLocalProvider`, `IsLocalHttp`, `RequiresApiKey`.
- They are **serialized into config.json on every save and ignored on load.** It's noise. Editing them does nothing.
- `RequiresApiKey` is false only for CrispASR presets and localhost `Http` providers, so a keyless server elsewhere on the LAN is refused ("No API key!").
- An ElevenLabs host resolves a blank auth header to `xi-api-key`, a blank model field to `model_id`, and bias to keyterms.

**What `LoadConfig` does after parsing, silently:**
1. **Appends missing shipped presets**, taking their API key (and, for ElevenLabs, keyterms and switches) from a same-host sibling (`InheritFromSibling`).
2. **Repairs superseded shipped values** (`RepairSupersededDefault`; today the Granite glob).
3. **Refills blank fields on shipped presets**: `LocalModelGlob`, `LocalBackendHint`, `LocalGpuBackend`, `LocalModelFolder`, `LocalServerPort`, `LocalPuncModel`, `LocalTruecaseModel`, `BiasMechanism` and `HotwordsBoost` from `CreateDefaults()`. Clearing one of these doesn't stick. `LocalExtraParams` is not refilled.
4. **It does not save.** New presets live in memory until the next `SaveConfig`.

> ⚠ **A bad hand-edit can wipe the API keys.**
> - One wrong-typed value or syntax error stops the load partway, logged as `Config error: …`.
> - `HasSeenFirstRun` then reads as false, and the first-run check **saves**.
> - That overwrites `config.json` with the defaults, or with only the providers read before the error.
>
> Edit config.json only while WhisperInk is closed, back it up first, and match the existing JSON types.

**The settings dialog also rewrites values** on every provider shown in it that session (it opens on the active one):
- The language list has 13 codes and no `auto`, so saving turns `auto` or `nl,en` into `en`.
- It strips a trailing `/` from `BaseUrl` and `TranscriptionEndpoint`. Smallest.ai's endpoint needs that slash.
- Saving calls `DropAll()`, so every WhisperInk CrispASR server restarts on its next take.

## 3.3 Keyboard hook (`KeyboardHookService.cs`)

- **Installation and callbacks.** `SetWindowsHookEx(WH_KEYBOARD_LL)` is installed from `MainWindow_Loaded`, so **its callback runs on the UI thread**. The delegate is held in a field: if it's garbage-collected, the hook dies silently. The callback only decides and queues (`Dispatcher.BeginInvoke`), because Windows drops a hook that is too slow (`LowLevelHooksTimeout`).
- **The hotkey.** Left or right Ctrl held, then Space down, starts a take: the hook turns suppression on at once, captures `GetForegroundWindow()` and swallows the Space. Space first, then Ctrl, does nothing.
  - While a take is active, every physical Ctrl and Space event is swallowed.
  - The first key-up of either ends the take.
  - Suppression ends once both keys are up. All other keys pass through.
- **Self-injection filter.** WhisperInk's own modifier key-ups carry the marker `0x5AFE` (`TextInjector.SyntheticMarkerValue`) in extra-info, and the hook ignores them. The Ctrl+V it sends is deliberately *not* marked.
- **Watchdog.** A 30 s timer: if no key event has reached the hook for 30 s but `GetAsyncKeyState` says a key 0x08–0xFE was pressed since the last check, the hook is reinstalled and `[hook-watchdog]` logged. Two things cause that: a `LowLevelHooksTimeout` drop, or another app re-hooking ahead of us.
  - Mouse buttons are excluded; including them once caused 377 needless reinstalls in a session.
  - **Known false positive:** the sweep is skipped while the hook is seeing keys, so their "pressed since" bits survive and trip the first idle check afterwards. That reinstalls the hook after 30–60 s of quiet, which is harmless ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)).
- **Recording state.** `_recState` is 0 idle, 1 recording, 2 stopping, changed only by `Interlocked.CompareExchange`. A double start or double stop is structurally impossible.

## 3.4 Capture pipeline (`MicCapture.cs`)

Opening a device on the hotkey path used to cost three measured latencies, all fixed by never doing it:

| Symptom | Cause (measured on the desktop) | Fix |
|---|---|---|
| Lag on the beep | `SoundPlayer.PlaySync()` took 190–222 ms for a 30 ms tone | `UiSoundPlayer`: one persistent `WaveOutEvent`; enqueue takes 0.02–0.46 ms |
| First syllable clipped | Press to first audio callback took 131–159 ms | **Warm mic + pre-roll ring**: the recording starts with audio from *before* the press |
| Sluggish stop | `StopRecording()` + `Dispose()` took 112–134 ms | The device is never stopped per take; only the writer detaches |

- **Warm mic.** `WaveInEvent` (16 kHz mono 16-bit, 50 ms buffers) stays open, and every buffer goes into a `PreRollRing`.
  - `BeginCapture` creates the writer, seeds it with the newest `PreRollMs` (400) and flips `_capturing`, all under one lock (`_gate`). Measured at 2 ms.
  - The cost of staying warm is the Windows mic-in-use indicator. Hence the idle release after `WarmMicIdleSeconds` (180; 0 = never) and the 🎙 Microphone ▸ Instant start toggle.
- **When the mic is cold.** `WarmMic()` opens the device at startup, so it's cold only after the idle release, after a device error, or with Instant start off. `BeginCapture` then opens it on the spot (~130 ms) with **no pre-roll**, so the first syllable can be clipped. The log says `mic was cold`.
- **"Instant start" OFF doesn't hold.** With `WarmMicEnabled` off, the device still opens at the first take and the idle timer never runs, so it stays open for the session ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)).
- **A `PreRollMs` change** takes effect the next time the device opens.
- **`PreRollRing`** is a separate class so its wrap arithmetic can be tested without a mic. An off-by-one there wouldn't crash; it would silently mis-order the start of every take. A byte-exact check against a reference model (400 random trials × 40 writes) was run once during development, but **it isn't in the repo**. Adding it to the harness is on the backlog.
- **Post-roll.** `EndCapture(postRollMs)` waits up to 80 ms for the in-flight buffer, so fixing the start didn't clip the end.
- **The stream sink.** `BeginCapture(sink)` gets exactly the WAV's bytes, in order, under `_gate`: the pre-roll, then each buffer. A sink that throws is detached and logged, and the WAV never depends on it.
- **Mic failure.**
  - A device error mid-take sets `LastCaptureInterrupted` and raises `onCaptureLost` immediately: Error tone and "🎙 MIC LOST" while the user is still talking.
  - A device that silently stops delivering shows up as a capture ≥ 1 s shorter than the hold. Normally a capture is *longer*, by the pre-roll. The device is then reopened, because a stalled one never recovers and its frozen pre-roll would seed the next take.
- **The silence gate (`SpeechDetector.cs`).** A take is dropped as silent only when **both** of these hold:
  - its whole-take RMS is under `SilenceThreshold` (0.003). RMS, not peak: two silent-room captures peaked at 0.0123 and 0.0124 on a single fan or key transient, with RMS 0.00060 and 0.00124;
  - it has **no sustained speech**: no run of three 30 ms frames (90 ms) that are each at least 3× the take's own noise floor (its 5th-percentile frame) and at least 0.002 RMS. Steady noise never makes a run however loud it is, and a click is a single frame.

  **Why both.** On 2026-09-23 the RMS test alone dropped three takes of real, quiet speech (RMS 0.0017–0.0026, peaks 0.031–0.043), and they were lost. The 0.003 default had assumed speech averages 0.01–0.1 RMS. That evening the owner's accepted takes averaged 0.0032–0.030, most of them 0.003–0.007, on the VEC USB mic at 94% input level (+18.5 dB of a possible +22.5, so there's little gain left to add). An average also dilutes: a long hold with a short phrase in it averages down toward the noise.
  - **Calibration (2026-09-23)**, on nine recordings of the owner's voice (three takes from that evening and the six clinical clips):
    - found in all nine at full, half and quarter volume, and buried in 20 s of their own noise floor;
    - found in all nine trimmed to just the speech, as a cold-mic take looks (no pre-roll, released on the last word), at a whole-take RMS of 0.003, 0.0026, 0.002 and 0.0015;
    - found in 34 of 36 one-second windows cut from *inside* the speech. A 10th-percentile floor at 4× found 31, which is why it's the 5th at 3×;
    - no speech found in steady noise up to 0.0025 RMS, noise swelling ±50%, or clicks. These negative cases are synthetic (harness section 0f).
    
    A voice with no quiet moments at all can't be told from steady noise this way; real speech has gaps.
  - **The trade-off.** A hold with only a burst of 90 ms or more in it (a cough, a door) now reaches the provider. ElevenLabs bills it (at least 20 s with over 100 keyterms), and a local model could invent text for it. If that shows up in practice, require more speech (`SpeechMs`) before sending.
  - **A take judged silent is kept**, the newest 5, under ↻ Unsent → 🔇 Judged silent. A hold of 1.5 s or more gets the Warn tone instead of the quiet blip, since nobody holds the key that long by accident.
  - **Where the levels are logged:** `[diag] captured …ms, RMS …, peak …, floor …, speech …ms` for takes that pass; in the `[skip]`/`[error]` line for silent or zero-signal takes; not at all for taps.
  - It **fails open**: an unreadable WAV measures as full scale and never silent, so the gate never drops audio it couldn't measure.
  - It runs on the thread pool, not the UI thread.
- **"No text" is judged by the same test.** Empty text is benign only for a take the detector itself calls silent, which can only reach a provider with the gate disabled: the quiet-take cue, kept under Judged silent. Anything with sound in it (loud enough, or sustained speech however quiet) is a failure: Error, "No text!", take kept. Real sound went in and nothing came out, which is how a broken backend presents.
- **No lockout.** `_recState` stays at Stopping for the whole stop path. A `Task.Delay(1500)` that once ran inside it caused ~2 s dead-hotkey windows after mis-presses. Transient status now goes through `FlashStatus`, a Background-priority one-shot timer.

## 3.5 Reliability: never lose a dictation

This was ported from `praxeo/elevenlabs-web`, the clinical web app (`worker.js`: `handleTranscribeBatch`, `cleanTranscript`, `coverageShortfall`; `keyterms.js`). That repo and its Cloudflare worker are **read-only reference**, in production at work: read them, never change them for WhisperInk.

- **Deadlines scale with the take.** `TranscriptionDeadline.For(provider, seconds)` is passed as the `CancellationToken` to every `TranscribeAsync`.
  - Budgets: cloud gets 20 s + 20 s per minute of audio (capped at 15 min); local or loopback gets 180 s + 1× the audio (capped at 2 h).
  - The shared clients time out after `HttpBackstop` (2 h 10 min), so the token always governs.
  - The flat limits this replaced (15 s cloud, 120 s CrispASR, 120 s Soniox poll) were length limits in disguise.
  - When a deadline fires: `[error] … did not finish within its Ns deadline`, the Error tone, "Timed out — saved ↻", a balloon, and the take is kept.
  - **Two exceptions:** Google Chirp 3 ignores the token and keeps its own 30 s limit, and `HttpTranscriber` has no separate arm for a client timeout ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)). The other cloud transcribers separate "the take's deadline" from "the HTTP request timed out".
  - Measured basis, from ~100 takes: ElevenLabs ≈ 0.45 s + 1.13 s per minute of audio, before the connection work; warm Voxtral 4B ≈ 0.11 s per second of audio; the first local take pays 1.5–3 s of server start; a WAV is 1.8 MB per minute.
- **The journal** (`UnsentTakes.cs`).
  - For every take that passed the guards, `Begin` queues the WAV and JSON sidecar write to `%APPDATA%\.WhisperInk\unsent\` on the thread pool, before transcription starts. It doesn't wait for the write.
  - `Delivered` deletes them. `Keep` records `failed` or `incomplete` with a reason. The stop path's `catch` keeps an unresolved take.
  - At startup, `Recover()` turns anything still `pending` into `interrupted` (the app closed, crashed or rebooted mid-take) and prunes to 14 days / 50 takes. A balloon says how many are waiting.
  - Takes the silence gate drops are kept too, with status `quiet`: the newest 5 (`MaxQuietCount`), on their own allowance so they can never push a real failure out. They're listed under ↻ Unsent → 🔇 Judged silent and left out of the startup count. A retry of one that comes back empty stays there ("Nothing heard", Dismissed) instead of becoming a failure, and one whose retry fails also keeps its place.
  - The folder is deliberately not OneDrive-synced, because this is clinical audio.
- **↻ Unsent dictations** (both menus) lists the newest 10 takes, each with:
  - *Retry with {active provider}*;
  - *Retry on {local model} (local fallback)*, for each local preset whose exe and GGUF are on disk;
  - *Show audio file*.
  
  A retry puts the text on the **clipboard** (the original window is long gone) and in History, then deletes the take. A fallback retry adds the Warn tone and a balloon naming the model, and its server is dropped afterwards. Retries go through the same `_recState` gate as dictation.
- **Incomplete transcripts** (`TranscriptCoverage.cs`; ElevenLabs only so far). It warns when the last transcribed word ends more than max(20 s, 25% of the take) before the last speech in the WAV, or when `audio_duration_secs` falls that far short.
  - "Speech" means 30 ms frames over 0.01 RMS, three in a row.
  - It is measured from the last *speech*, never from key-up, so holding the key after finishing can't warn.
  - Missing timing only ever means "no warning".
  - The text is still pasted, with Warn, "⚠ Check the end!", a balloon, and the take kept.
- **The previous session's log survives.** `debug.log` is moved to `debug.previous.log` at launch, or copied if the move fails. It used to be truncated, and the usual reaction to a failure — a restart — destroyed the evidence.
- **Crash logging.** `App.WriteCrash` logs `Exception.ToString()`, including inner exceptions for `UnobservedTaskException`. `RunSafe` logs the base exception's type and stack.

## 3.6 Cloud connections and the streamed upload

**One shared `HttpClient`** (`MainWindow.CreateCloudHttpClient`) serves every cloud transcriber except Google Chirp 3.

**The pool and keep-alive.**
- Idle connections are kept **9 minutes**; .NET's default is 1.
- Every socket gets TCP keep-alive: the first probe after 45 s idle, then every 5 s, with 3 misses allowed. So a NAT or firewall that silently drops an idle mapping can't turn the next request into a hang lasting until the deadline.
- Each new connection is logged: `[net] new connection to host:443 (N ms to connect)`.

**Pre-warm.** `PrewarmConnection` sends a fire-and-forget `HEAD` to the provider host's root at key-press, capped at 5 s and logged as `[net] pre-warmed host (status) in N ms`. It's skipped for local servers, Chirp 3 and streamed takes.

**The streamed upload** (`StreamedTranscription.cs`) is ElevenLabs only, controlled by `StreamUpload` (default true).

1. At the press, `BeginStreamedTake` opens the ordinary request: the same fields, from `HttpTranscriber.BuildFields`.
2. The audio part is a queue-backed stream, sent **chunked** as raw PCM with `file_format=pcm_s16le_16`. With no WAV header, the length needn't be known up front.
3. `MicCapture` feeds it the same bytes it writes to the WAV.
4. At release `FinishAsync` ends the body and waits for the transcript under the take's deadline.

Safety rules:
- **Byte-exact or nothing.** The result is used only if the stream carried exactly the WAV's PCM bytes (`PcmLength`). That's checked *before* ending the body, so a short stream is cancelled rather than transcribed.
- **Fallback.** Any failure other than the deadline sends the WAV the ordinary way, under the same deadline. That includes an HTTP error, a transport error, a missing text field, or the provider or transcriber changing mid-hold. The WAV is journaled first, as always.
- **Discarded takes cost nothing.** A tap or silent take disposes the stream, which cancels it mid-body, so the service never gets a complete request. Measured on 2026-09-23 against the account's usage counter: +64 for one normal request, +0 for three cancelled streams.
- **Threading.** The send runs in `Task.Run`, because HttpClient's first request can block on proxy auto-detection, which on the press path would eat audio past the pre-roll. The capture thread only copies and queues.
- **Cap.** Past 5 minutes of audio, streaming is abandoned for the ordinary upload. 104 s was the longest take tested.
- **Logging.** `[stream] … upload opened at the key-press`, `… N bytes streamed …`, `… sending the take as a file instead (reason)`, `… upload cancelled`. The timing line reads `took Nms after release (streamed)`.

**Measured on 2026-09-23** (`_scratch\scribe-latency`, JFK plus a TTS clip). ElevenLabs sits behind Google's front end, with an edge ping of 8–11 ms, and is served from `us-central1`. The desktop uploads at ~2.5 MB/s.

| What | Result |
|---|---|
| Warm connection, 1–7 s take | ~340–400 ms. Length adds almost exactly its upload time |
| Fresh connection | +166 ms median (4 s take) |
| .NET's 1-minute pool | Dropped after every gap over 60 s: 9 of 14 real takes that evening went out cold (446–1,117 ms vs 341–383 ms warm) |
| Pre-warm at press | Recovers ~90 of the 166 ms; the rest is TCP slow start |
| Idle connection kept | Reused after 75 s (3/3), 5 min and 9 min |
| `scribe_v2_medical` vs `scribe_v2` | 88–129 ms faster |
| 249 keyterms vs none | ~0 ms at 4 s, +110–140 ms at 11–34 s |
| `file_format=pcm_s16le_16` alone | No gain (+16 to +42 ms), despite the docs |
| Streamed vs pre-warm + POST, Medical | 4 s: 240 vs 323 ms after a pause, 246 vs 307 warm. 11 s: 307 vs 402 and 309 vs 393 (medians of 5). No fallbacks |
| Real streamed takes that evening | 804 ms (first after restart), then 280–523 ms. Server variance of ±150 ms swamps small samples |

## 3.7 Delivery (`TextInjector.cs`, `MainWindow.DeliverAsync`)

- **The verified target.** `DeliverAsync` calls `SetForegroundWindow` if needed, then waits up to 10×15 ms for the take's own window to be in front, and only then pastes. Otherwise the text goes to the clipboard, with a `[warn] not pasted …` line that logs window handles only.
- **The paste.** Clipboard set, a synthetic Ctrl+V, and a leading space to avoid fusing words. The prior clipboard is cloned and restored `ClipboardRestoreMs` (1 s) later. Chained takes reuse the pending saved copy, so the user's original clipboard survives rapid dictation.
  - **Why 1 s.** The target app reads the clipboard whenever it gets round to handling the Ctrl+V, and one that reads it *after* the restore pastes the user's old clipboard instead of the dictation. A busy window (Electron mid-render, an EHR over Citrix) can take longer than the 250 ms used until 2026-09-23. Suspected, not proven: that evening, log excerpts the owner had copied arrived in a Claude chat in place of dictations.
  - **Never clobber a newer copy.** The restore is skipped if anything else has written the clipboard since our own write (`GetClipboardSequenceNumber` moved), logged as `Paste: the clipboard changed after the paste — not restored`. A chained paste reuses the pending saved copy only if nothing was copied in between; otherwise it saves the newer copy. A restore overtaken by a newer paste stands down.
  - **Watch for:** that "not restored" line on *every* paste. It would mean something (a clipboard manager, Citrix or RDP redirection) writes the clipboard after each paste, leaving the dictation on it instead of the user's copy. Unverified either way as of 2026-09-23.
- **`CopyToClipboard`** puts the text alone on the clipboard, with no paste and no restore. It cancels any pending restore, which would otherwise overwrite it. Used for undeliverable takes and retries.
- **`ReleaseAllModifierKeys`** runs at press and in the stop path's `finally`. The hook swallows the physical key-up, so without it Ctrl stays down.

## 3.8 UI: menus, health, history, bundle

**The bar.**
- 220×38, borderless, Topmost, not in the taskbar. Placed at the bottom-right of the work area; draggable, but the position isn't saved.
- **Idle:** a health dot plus the provider name.
- **Recording:** a red border, "🎙 REC", and a 10-bar "histogram" that is a **random animation**, not driven by the audio.
- **Status flash:** transient text (`FlashStatus`, Background priority; it never overwrites "🎙 REC").
- **Setup banner row:** grows the bar to 68 px.
- Closing the bar **hides it to the tray**, and the keyboard hook stays live. Only ❌ Quit exits (or closing, when `QuitOnClose` is set). **There's no single-instance guard.**

**The menu.** One `MenuNode` tree built by `BuildAppMenu()` and rebuilt on every open. The tray (WinForms) and the bar's right-click menu (WPF) render the same tree.

| Item | Surface | What it does |
|---|---|---|
| Header "{dot} Active provider: {name}", tooltip = health summary | Tray only | Disabled; information only |
| Show Window | Tray only | Shows and restores the bar. Tray left-click and double-click do the same |
| 🔌 Provider: {name} ▸ one item per provider | Both | `SwitchProvider`: drops the old transcriber, saves, re-probes health |
| 🔌 Provider ▸ ⚙ Configure Providers... | Both | Settings dialog; on save, `DropAll()` and save |
| 🎙 Microphone ▸ one item per device | Both | Re-listed on every open; selecting one reopens the mic |
| 🎙 Microphone ▸ ⚡ Instant start | Both | Toggles the warm mic |
| 🔊 / 🔇 Sound | Both | Mutes the tones |
| 🎯 Context Bias Terms | Both | Modal editor for the shared list |
| 📋 History | Both | The history window: HH:mm times only, Copy and Delete per row, no search |
| ↻ Unsent dictations (N) ▸ | Both | The newest 10 takes, each with Retry with {active}, Retry on {local} (local fallback) and Show audio file; then an overflow line, 🔇 Judged silent (M) ▸ with the same actions for the takes the silence gate dropped (not counted in N), and 📂 Open unsent folder |
| 🖥 Local GPU backend ▸ Auto / Vulkan / CUDA / CPU | Both | Tooltip = the GPU probe. A change runs `DropAll()` |
| 📂 Open config folder / Open debug log / Open model folder | Both | "Model folder" is always `cohere-gguf`, even if a preset sets `LocalModelFolder` |
| Copy support bundle | Both | See below |
| Diagnose active provider | Both | A modal report: exe and model found or missing, a port check, and a DLL list that still checks the pre-consolidation names `cohere.dll` and `parakeet.dll` |
| About… / View README | Both | |
| Quit on close ✓ / Launch at Windows start ✓ | Both | Toggles |
| ⬇ Hide to tray | Bar only | |
| ❌ Quit | Both | Exits for real |

**Health** (`HealthProbe`):
- **When:** at start, every 60 s, on a provider switch and on a settings save.
- **Local CrispASR:** "Missing: …" if the exe or model is absent. If the port isn't listening, that's still OK ("not yet spawned"). If it is, `GET /health`.
- **Localhost Http provider:** `GET /health`.
- **Cloud:** only checks that a key is present. There's no network call, so a wrong key shows green.
- **Where the result shows:** the dot (🟢 / 🔴 / 🟡), its tooltip, the tray tooltip (63 characters), the tray header, and a banner on failure ("Setup needed: … — click to fix", which opens the model folder or the settings).
- **Port choice:** HealthProbe takes the port from `BaseUrl` first, while the spawner takes it from `LocalServerPort` first. They agree for every shipped preset.

**History** (`HistoryService`):
- `history.json`, newest first, capped at 100, rewritten on every change.
- No age limit and no encryption.
- Every delivery is added, including clipboard-only ones and successful retries.

**Support bundle** (`SupportBundle`):
- **Where:** `Desktop\WhisperInk-support-<stamp>.zip`, which is OneDrive-synced, also placed on the clipboard as a file.
- **Contains:**
  - `about.txt`: versions, providers (base URL, whether a key is set, model), and a listing of `cohere-gguf`;
  - the last 500 lines of `debug.log` and `debug.previous.log`;
  - `config.json` with `ApiKey` and `MistralApiKey` redacted.
- **It carries transcript text** (the log tails) and the unredacted vocabulary.
- **If config.json doesn't parse, it's included raw, keys and all.**

**Startup** (`MainWindow_Loaded`), in order:
1. Rotate `debug.log`.
2. Place the bar.
3. Install the hook and its watchdog.
4. `LoadConfig`. On a first launch this can show a modal message.
5. Create the sound player, then open the mic (warm).
6. Create the take journal, then the transcriber factory.
7. Tray, health probe, GPU probe, local-model banner.
8. Sync `LaunchAtStartup` from the registry.
9. First-run balloon.
10. Unsent-take recovery and its balloon.

## 3.9 Logging

`debug.log` lines are `[HH:mm:ss.fff] message`; each session starts with `=== WhisperInk started yyyy-MM-dd HH:mm:ss ===`. `Log()` is static, appends without a lock, and swallows its own errors.

| Prefix | Meaning | Example |
|---|---|---|
| `[diag]` | Pipeline tracing | `[diag] captured 6950ms, RMS 0.00856, peak 0.1051, floor 0.00031, speech 5130ms` |
| `[error]` | A loud failure: a deadline, no text for real sound, or a mic failure. **A failed request logs none**; its evidence is the transcriber's `HTTP` or error line, then `[unsent] kept … (<reason>)`. Mic failures are never journaled | `[error] … did not finish within its 21s deadline …` |
| `[warn]` | Delivered, but check it | `[warn] not pasted: the window … (0x…) is not in front …` |
| `[skip]` | A deliberate discard | `[skip] held 180ms < 250ms — discarded`, `[skip] 2450ms of audio, RMS 0.00043 < 0.00300 and no sustained speech (…) — nothing said, no transcription; kept under ↻ Unsent → Judged silent` |
| `[net]` | Cloud connections | `[net] new connection to api.elevenlabs.io:443 (20 ms to connect)`, `[net] pre-warmed … (404) in 45 ms` |
| `[stream]` | The streamed upload | `[stream] elevenlabs-medical: 110400 bytes streamed (3.5 s); waiting for the transcript` |
| `[mic]` | The capture device | `[mic] released after 180s idle` |
| `[unsent]`, `[retry]` | The journal and retries | `[unsent] kept take-… (1.0s, …) for a retry: …` |
| `[keyterms]`, `[scribe]` | The ElevenLabs request | `[keyterms] sending 249 terms`, `[scribe] last word ends at 10.5 s; decoded 11.0 s of audio` |
| `[hook-watchdog]` | Hook reinstalled | Some after quiet periods are false positives ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)) |
| `[unhandled]` | A fault caught by `RunSafe`, with its stack | |
| `[sound]`, `[deepgram]`, `[soniox]`, `[modulate]`, `[smallest]`, `[reson8]`, `[chirp3]` | Subsystem and provider notes | |
| `[<providerId>] HTTP <code>: <first 500 chars>` | **Every** Http-kind response, **including 200s, which carry the transcript** | |
| `CrispAsr(<id>): …` | Local-server lifecycle and failures, with the last 20 output lines | |
| `<Name> took Nms[ after release (streamed)] on Mms audio = RTFx … -- result: <first 200 chars>` | Per-take timing, **with the transcript's start** | |
| `Batch pipeline: capture=… transcribe=… paste=… TOTAL=…` | The end-to-end split | |
| `Active provider: <name> → STT=<url> (auth=…, modelField=…)` | At start and on switch. Check the auth scheme here | |
| `!!! <source> !!!` | Crash handlers (App.xaml.cs) | |

Transcripts in the log are a known privacy gap: the support bundle ships them. Log **window handles, never titles**; titles can carry patient names.

## 3.10 Dispatch and the transcriber contract

- **`TranscribeTakeAsync(provider, wav, audioMs, streamed)`** is the single entry point, for both live takes and retries.
  1. It gets `_transcribers.GetOrCreate(provider)`, the cached instance, then `IsReady`.
  2. It builds the deadline token (`TranscriptionDeadline.For`) and calls `TranscribeAsync(wav, _contextBiasTerms, ct)`, or, for a streamed take, `FinishAsync` with a fallback to `TranscribeAsync`.
  3. It reads `ITranscriptCoverage` from the same instance.
- **`TranscriberFactory`** caches one instance per provider id.
  - `Drop(id)` on a provider switch, so ~2 GB of local model doesn't stay resident.
  - `DropAll()` after the settings dialog or a GPU-backend change.
  - A cached instance keeps its resolved state: a CrispASR transcriber resolves its GGUF once, so a model added after a failed take isn't seen until the transcriber is recreated.
- **The contract for a transcriber:**
  - Return the text, `""` for genuinely nothing, or `null` for a failure.
  - Honour `ct` on every request.
  - Never throw for ordinary failures; log them.
  - Take string fields before the file part for multipart.
  - Six cloud transcribers (Soniox, Deepgram, Chirp 3, Modulate, Smallest, Reson8) turn an empty transcript into `null`, which MainWindow reports as a failed request ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)).

---

# Part 4 — Providers

## 4.1 Catalog

The shipped presets (`ApiProvider.CreateDefaults()`). "Status" is the desktop as of 2026-09-23. Keys live in each machine's `config.json`, never in the repo.

| Id | Kind | What it is | Status and verdict |
|---|---|---|---|
| `elevenlabs-medical` | `Http` | Scribe v2 **Medical** (`scribe_v2_medical`), keyterms, streamed upload | **Active.** Fastest measured and 6/6 on the clinical clips without keyterms. Tends to drop final periods |
| `elevenlabs` | `Http` | Scribe v2 (`scribe_v2`), keyterms, streamed upload | The default until 2026-09-23. The fallback if Medical's punctuation bothers |
| `deepgram-medical` | `Deepgram` | Nova-3 Medical, keyterm prompting | Key set. Strongest *unbiased* clinical model in the June tests, and the only one that got `ureterolithiasis` natively |
| `deepgram` | `Deepgram` | Nova-3 general | Key set |
| `soniox` | `Soniox` | `stt-async-v5` (async job), `context.terms` | Key set, auth-verified 2026-06-12. Batch only |
| `google-chirp3` | `GoogleChirp3` | Chirp 3 (STT v2), `phraseSets` | Configured with a service-account JSON, verified working |
| `modulate`, `modulate-english-fast`, `modulate-multilingual-fast` | `Modulate` | Velma 2 batch; the model is the endpoint path | Key set. **Rejected 2026-08-29**: a 6× latency variance spike, 0/5 first-word capitalization, and English Fast discards bias terms |
| `smallest-pulse-pro`, `smallest-pulse` | `Smallest` | Waves Pulse Pro (English) and Pulse (multilingual) | Key set. **No biasing surface at all.** Pulse Pro wrote *hematemesis* for *hematochezia* — avoid for clinical work |
| `reson8` | `Reson8` | Prerecorded, `phrases` biasing, `custom_model_id` | Key set. **Live-tested 2026-08-29**: fixed 3/3 hard terms with a 3-term list, but the full 17-term list turned one *hematochezia* into *hematemesis*. Warm latency 686–926 ms. Keep its list tight |
| `mistral` | `Http` | Voxtral batch, `context_bias` (≤100) | No key on the desktop |
| `openai` | `Http` | `whisper-1`, prompt glossary | No key on the desktop |
| `cohere-api` | `Http` | Cohere Transcribe v2 cloud, temp 0.1, **no biasing field** | No key on the desktop |
| `qwen3-asr-1.7b-local` | Local, port 8112 | Qwen3-ASR 1.7B q4_k | **Best local preset.** Biasing works: 3/3 hard terms, controls untouched |
| `parakeet-rnnt-local` | Local, 8109 | Parakeet RNNT 1.1b q4_k + FireRedPunc (`fullstop`) | Gets *hematochezia* natively. The best CPU option (laptop) |
| `parakeet-local` | Local, 8103 | Parakeet TDT 0.6b v3 q4_k | Weak on the hard terms even with a boost |
| `cohere-local-q6k` | Local, 8105 | Cohere Transcribe Q6_K | Misses both hard terms. Biasing is a no-op |
| `cohere-gguf-server` | Local, 8766 | Cohere, pinned to CPU | CPU fallback. Its glob `cohere-transcribe-*.gguf` is loose ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)) |
| `voxtral4b-local` | Local, 8108 | Voxtral Mini 4B Realtime | Biasing is a no-op |
| `voxtral-local` | Local, 8106 | Voxtral Mini 3B | **No 3B GGUF on the desktop**, so it can't run there |
| `granite-local` | Local, 8107 | Granite Speech 4.1 2B | Biasing is real but **not chart-safe** (rewrote a correct term). All-lowercase output |

GGUFs on the desktop, in `cohere-gguf\`:
- cohere-transcribe q6_k;
- granite-speech-4.1-2b q4_k and 2b-plus q4_k (no preset uses plus);
- parakeet-rnnt-1.1b q4_k and parakeet-tdt-0.6b-v3 q4_k;
- qwen3-asr-1.7b q4_k;
- voxtral-mini-4b-realtime q4_k;
- gemma4-e2b-it q8_0 (not a WhisperInk model).

## 4.2 Which provider for what

- **Everyday clinical dictation:** `elevenlabs-medical`. It had the best clinical accuracy observed and the lowest measured latency (streamed, ~250–500 ms from release), at the same price as Scribe v2.
  - Watch items: final periods are dropped more often than with Scribe v2; a keyterm at the start of a sentence keeps its list casing ("periwound" came out lowercase); words not in the keyterm list can go astray ("ElevenLabs" came out as "11 labs").
- **Second opinion or fallback:**
  - `deepgram-medical`, the strongest unbiased clinical model in June's tests;
  - `elevenlabs` (Scribe v2), for more consistent sentence punctuation.
- **Offline or local:**
  - with a GPU, `qwen3-asr-1.7b-local`: ~160–290 ms a clip warm on CUDA, and the only local preset whose biasing is both real and safe;
  - on CPU, `parakeet-rnnt-local`.
- **Don't use for clinical text:**
  - Smallest.ai: no biasing, and a clinically opposite substitution;
  - `granite-local`: biasing rewrote a correct term, and the output is lowercase;
  - Modulate: rejected.

Switching providers is one click (🔌 Provider in either menu). The outgoing local model's server is dropped so ~2 GB doesn't stay resident.

## 4.3 Context biasing

**One vocabulary, routed natively.** The user maintains one shared list: `ContextBiasTerms` in config.json, held as `MainWindow._contextBiasTerms` and edited in the 🎯 Context Bias window. Each provider sends it through its own native mechanism (`ApiProvider.BiasMechanism`, baked into each preset). ElevenLabs also merges its own long list, `ScribeKeytermsRaw`. Long specialty lists belong there, not in the shared list: the shared list reaches every provider, most cap it at 100, and speech-LLM prompts dilute with length.

| Mechanism | Providers | Field and limits | Effect (measured) |
|---|---|---|---|
| `elevenlabs_keyterms` | ElevenLabs (both) | Repeated `keyterms`. ≤1000 terms, <50 chars, ≤5 words; `` ` < > { } [ ] \ `` dropped. **Over 100 terms, each request bills ≥20 s**, plus a 20% surcharge | Real. Scribe v2 without keyterms inserted a stray word; Medical needed none on the clips. Adds ~110–140 ms on takes ≥11 s |
| `deepgram_keyterm` | Deepgram (both) | Repeated `keyterm` query params (Nova-3; legacy `keywords` otherwise). Capped at 100 | Real |
| `context_terms` | Soniox | `context.terms`. Capped at 100 | Real |
| `phrase_sets` | Google Chirp 3 | Inline `adaptation.phraseSets` | Real |
| `modulate_custom_terms` | Modulate Multilingual only | `custom_terms` inside the JSON `config` field. ≤1000 entries, clamped at 7800 chars | Real; the Fast endpoints have none |
| `reson8_phrases` | Reson8 | Comma-joined `phrases` query param. ≤250 terms, 4000-char budget, commas in terms become spaces | Real. A tight list fixed 3/3; **the 17-term list degraded a hard term into its opposite** |
| `mistral_context_bias` | Mistral | Comma-joined `context_bias`. ≤100 | Not measured |
| `whisper_prompt` | OpenAI | `prompt` = "Glossary: a, b, c." | Not measured |
| `hotwords` | Local CrispASR | Comma-joined `hotwords` form field. **Two different mechanisms:** a boost trie on Parakeet (`HotwordsBoost`, off by default: ≥8 garbles neighbours), and prompt text on the speech-LLMs (Qwen3-ASR and Voxtral 3B: "…may appear in the audio"; Granite: " Keywords: …") | Qwen3: real and safe. Granite: real, unsafe. Parakeet: weak. Cohere and Voxtral 4B: ignored |
| `none` | Smallest.ai, Cohere cloud | — | Terms can't be routed anywhere. A mis-heard term on these can't be corrected |

**How much `BiasMechanism` actually controls:** only `HttpTranscriber` reads it.
- The native cloud transcribers (Deepgram, Soniox, Chirp 3, Modulate, Reson8) route the list their own way regardless.
- `CrispAsrServerTranscriber` sends `hotwords` **whenever the list is non-empty**, so `"none"` on a local preset changes only the label in the settings dialog.
- A user-added `Http` provider with no `BiasMechanism` falls back to the legacy `ContextBiasMode`. The exception is ElevenLabs (`IsElevenLabs`), which always gets keyterms.

**The six clinical clips** live in `_scratch\biasing\clips\`. They are the owner's own voice (never TTS: the misses are speaker- and acoustics-specific) and are git-ignored; `RECORD_THESE.md` has the script. They are the standing accuracy test: `hematochezia_1` and `_2`, `ureterolithiasis` (hard terms), and `biliary_colic`, `ureteral_colic` and `neutral` (controls). What's been measured on them:

| Provider (date) | hematochezia ×2 | ureterolithiasis | Controls |
|---|---|---|---|
| `qwen3-asr-1.7b-local`, no terms (08-29, 09-23) | ✗ hematuria / hematemesis | ✗ "bursitis with edema" | ✓ |
| `qwen3-asr-1.7b-local` + terms (08-29, 09-23) | ✓✓ | ✓ | ✓ untouched |
| `granite-local` 2B + terms (09-23) | ✓✓ (✗ without) | ✓ (✗ "ureteral atresia" without) | ✗ "ureteral colic" became "ureterolithiasis"; lowercase output |
| Granite 2b-plus + terms (09-23) | ✗ hematemesis | ✓ | ✗ garbles `ureteral_colic` |
| `elevenlabs` Scribe v2 + keyterms (09-23) | ✓✓ | ✓ | ✓ (without keyterms: a stray "your") |
| `elevenlabs-medical`, with or without keyterms (09-23) | ✓✓ | ✓ | ✓ |
| `deepgram-medical` (06-14) | Not recorded ("strictly better" than general Nova-3 overall) | ✓, the only June provider to get it (general Nova-3 mangled it) | ✓, no hallucination on `neutral` |
| `parakeet-rnnt-local` (06-14) | ✓ natively | ✗ | ✓ |
| `parakeet-local` TDT 0.6b, boost ≤10 (06-14) | ✗ | ✗ | Garbles at boost ≥8 |
| `cohere-local-q6k` (06-14) | ✗ "hematokesia" | ✗ "ureter with Isis" | ✓; biasing byte-identical |
| `smallest-pulse-pro` (08-29) | ✗ **hematemesis** (clinically opposite) | ✗ | ✓ |
| `smallest-pulse` (08-29) | ✗ "hematochesia" | ✗ "ureterithiasis" | ✓ |
| `reson8`, no phrases (08-29) | ✗ "hematokesia" ×2 | ✗ "ureter with Isis" | ✓ |
| `reson8` + 3 targeted terms (08-29) | ✓✓ | ✓ | ✓ unchanged |
| `reson8` + the 17-term list (08-29) | ✗ **hematemesis** / ✓ | ✓ | ✓ |

**How list length behaves**, which differs by provider:

| Behaviour | Providers | Effect of a long list |
|---|---|---|
| Ignored | Smallest.ai, Cohere, Voxtral 4B | Wasted |
| Dilutes | Qwen3 | A 40-term list still fixed 5 of 6 |
| Degrades | Reson8 | Worse transcripts, per upstream and measured: the 17-term list turned *hematochezia* into *hematemesis* |
| Costs | ElevenLabs | Over 100 terms, a 20 s billing floor per take |

Keep the shared list tight when a specific term matters.

## 4.4 ElevenLabs (Scribe v2, Scribe v2 Medical)

- **Request** (`HttpTranscriber`, gated on `ApiProvider.IsElevenLabs`, i.e. `xi-api-key` auth or an `elevenlabs.io` host). It mirrors what elevenlabs-web runs in production:
  - `model_id`;
  - `language_code` (omitted for `auto`; a bare `language` returns 422);
  - `temperature=0` unless configured;
  - `diarize=false` and `num_speakers=1`;
  - `timestamps_granularity=word` (feeds `TranscriptCoverage`);
  - `tag_audio_events` (sent explicitly because the API default is true) and `no_verbatim`;
  - keyterms.
  
  String fields come before the file part.
- **Auth resolution.** A blank `AuthHeaderName` on an ElevenLabs host resolves to `xi-api-key`, a blank model field to `model_id`, and the bias mechanism to keyterms (`ResolvedAuthHeaderName` and friends). A hand-added entry used to send Bearer and get 401.
- **Cleanup.** elevenlabs-web's `cleanTranscript`: pause ellipses and line breaks become spaces, space runs collapse, no space before punctuation (`HttpTranscriber.CleanElevenLabsText`).
- **Streamed upload** by default ([3.6](#36-cloud-connections-and-the-streamed-upload)): chunked raw PCM with `file_format=pcm_s16le_16`.
- **Medical** (`scribe_v2_medical`): GA 2026-09-11, same price, batch only. ElevenLabs claims 35% fewer clinical errors. It returns word timing.
- **Realtime** (`scribe_v2_realtime`) was evaluated and is unfit: keyterms are capped at 50 terms of ≤20 characters, ElevenLabs says batch is more accurate, it costs more, and there's no medical variant.
- **Routing.** Served from `x-region: us-central1` through Google's front end. `api.us.elevenlabs.io` only opts out of global routing and gains nothing here. Residency hosts are Enterprise-only.

## 4.5 Deepgram

- **Request** (`DeepgramTranscriber`): one POST to `api.deepgram.com/v1/listen` with the WAV as the raw body (`audio/wav`) and `Authorization: Token <key>`.
  - Options go in the query: `model`, `smart_format=true` (baked in), and `language` (omitted for `auto`).
  - Transcript: `results.channels[0].alternatives[0].transcript`.
- **Biasing**: repeated `keyterm` params (≤100) on Nova-3, legacy `keywords` on older models.
- **`DeepgramExtraParams`** passes anything else into the query, config only. Reserved keys: `model`, `smart_format`, `language`, `keyterm`, `keywords`.
- **`deepgram-medical` ships with no extra params on purpose.** A live A/B on clinical clips rejected every calibration knob for text pasted straight into a chart:
  - `dictation=true` stripped the final period and downcased `CT` to `Ct`. It is *not* reserved, so an opt-in passes through;
  - `measurements` risks ISMP-banned unit abbreviations;
  - `numerals` turned "cranial nerve two" into "2";
  - `filler_words` isn't supported on Medical;
  - `paragraphs` is covered by `smart_format`.
- **Quirk:** the API reports `arch: nova-3` for `nova-3-medical`. That's expected, not a fallback.

## 4.6 Soniox

No synchronous endpoint, so each take is a job on `api.soniox.com/v1` (Bearer), in `SonioxTranscriber`:

1. `POST /files`;
2. `POST /transcriptions` with `model`, `file_id`, `language_hints` and `context.terms` (≤100);
3. poll `GET /transcriptions/{id}` every 400 ms;
4. `GET …/transcript`, whose `tokens[]` are concatenated because each carries its own spacing.

A `finally` best-effort DELETEs the job and the file, each with its own 10 s budget, since the take's token may already be cancelled. Model `stt-async-v5` (v4 was removed 2026-06-30); it's a config value.

## 4.7 Google Chirp 3

`GoogleChirp3Transcriber`: STT v2 with OAuth from a **service-account JSON** (the `ApiKey` field holds a path to the file or its raw contents; it's not an API key).
- Base64 audio in a JSON body; bias terms go in inline `adaptation.phraseSets`.
- `BaseUrl` picks the region (`us-speech.googleapis.com` or `eu-…`).
- It has its own 30 s `HttpClient`: the sync API refuses audio over ~60 s, and it logs a warning above that. So it gets no pre-warm and no shared-pool benefits.
- The phrase list is capped at 1000 (`[chirp3] bias list truncated`).
- Pasting an `AIza…` API key fails at construction with `invalid JSON: 'A' is an invalid start of a value`.
- HTTP errors are logged as `GoogleChirp3 HTTP <code>: …`. They used to go to stderr and vanish; `9e745c7` fixed that.

## 4.8 Modulate (rejected, kept in code)

`ModulateTranscriber`: a multipart POST whose audio part is named `upload_file`, with the filename ending in `.wav` because the format is validated by extension. Auth is a bare `X-API-Key`, and the transcript is top-level `text`.

**The model is the endpoint path.** The three presets differ only by URL, and the transcriber derives the field set from it:

| Variant | Fields sent |
|---|---|
| Multilingual | `speaker_diarization=false` (the default is true), `language`, and `config.custom_terms` |
| English Fast | `upload_file` only |
| Multilingual Fast | `language` |

- The paths are `public const` on `ModulateTranscriber` and presets are built from them, so they can't drift.
- Every enrichment signal is off. `pii_phi_tagging` would paste entity tags into the chart.
- There is deliberately no passthrough.
- Auth failures are 401 on Multilingual Fast and 403 on the others; `DescribeError` names the cause.

## 4.9 Smallest.ai

`SmallestTranscriber`: raw-body POST to `https://api.smallest.ai/waves/v1/stt/`. The **trailing slash is required**. Bearer auth, query-param options, transcript at top-level `transcription`. `model` is a real query param (`pulse-pro` / `pulse`).

**`language` can kill every take, so it is always sent explicitly:**
- Pulse Pro accepts only `en`, and anything else is coerced to it.
- On Pulse, an omitted language falls back to `multi`, which is region-gated (400 `LANGUAGE_NOT_ENABLED_IN_REGION`). So `auto` maps to `multi-eu`, which is verified enabled.
- The real enum has 46 codes; the docs list 26.
- `DescribeError` surfaces `error_code`.

**Also:**
- **No biasing field exists.** The transcriber logs the ignored count on every take.
- All enrichments are off. `redact_pii`/`redact_pci` would paste `[FIRSTNAME_1]` tokens, and `webhook_url` would return no transcript.
- **Content is retained by default.** The opt-out header is Enterprise-only.

## 4.10 Reson8

`Reson8Transcriber`: raw-body POST to `https://api.reson8.dev/v1/speech-to-text/prerecorded` with `Authorization: ApiKey <key>`, a third auth scheme alongside `Token` and Bearer. Query-param options, top-level `text`. Errors are RFC 7807 `problem+json` with a lowercase `code`.

- **One preset.** There's no `model` param; the customization axis is `custom_model_id` (up to 50,000 phrases, built in their console).
- **Language:** auto-detect means *omitting* the param.
  - Only 10 languages are supported (`de en es fr fy it nl pl pt sv`), so `ResolveLanguage` drops unsupported codes, some of which WhisperInk's dropdown offers (`ja ko zh ru ar hi`), and logs it.
  - The preset pins `en`, because detection is unreliable on short utterances.
- **`Reson8ExtraParams`** passthrough, worth using for `custom_model_id`, `filler_mode` and `patterns`. Reserved keys: `language`, `phrases`, `encoding`, `sample_rate`, `channels`. The last three are reserved because we post a full WAV: declaring raw PCM would make the header play as noise.
- **Proof so far:**
  - `_scratch/reson8/` checks 53 wire-format properties against a local listener.
  - `run-clips.ps1` ran the six clips live on 2026-08-29 (CSV in `results\`), with the results in the clips table in [4.3](#43-context-biasing).
  - It's the provider where list length matters most: a tight list helped, a long one hurt.

## 4.11 Mistral, OpenAI, Cohere cloud

All three go through `HttpTranscriber` on OpenAI-style multipart.
- **Mistral** (Voxtral batch) gets `context_bias` (≤100, comma-joined).
- **OpenAI** `whisper-1` gets a "Glossary: …" prompt and temperature 0.
- **Cohere Transcribe v2** needs string fields before the file (a hard 4xx otherwise), temperature 0.1, and **has no biasing field**. The old `cohere_terms` was a phantom the server silently dropped.

## 4.12 Local presets (CrispASR)

Each local preset runs its own `crispasr.exe --server` on its own port ([Part 5](#part-5--local-asr-crispasr)). The notes that matter when choosing or changing one:

- **`qwen3-asr-1.7b-local`:**
  - The glob is pinned to `qwen3-asr-1.7b-*`, so a future 0.6b can't hijack it.
  - The backend hint `qwen3-1.7b` is pinned to document intent (auto-detect gives `qwen3`).
  - Native punctuation and casing, so no punctuation model.
  - The terms are spliced into the ChatML system turn as "The following words may appear in the audio: …". Upstream has no per-request "verbatim context" field.
- **`parakeet-rnnt-local`:**
  - It emits no punctuation, so `LocalPuncModel="fullstop"` (FireRedPunc, server-side) restores it.
  - `-t` is capped at `Min(8, ProcessorCount)`.
  - With v0.8.30, RNNT plus punctuation got 2.3× faster (749 → 325 ms).
- **`parakeet-local`** (TDT 0.6b):
  - The glob `parakeet-tdt-*` is pinned because `parakeet-*` would match RNNT first.
  - The boost trie never flipped a hard term. ≥8 garbles, so `HotwordsBoost` is off (null = server default 2.0, inert).
- **`cohere-local-q6k` / `cohere-gguf-server`:**
  - The backend hint `cohere` is required (metadata doesn't auto-detect).
  - `cohere-gguf-server` pins `cpu` (`-ng`), port 8766.
  - Biasing is byte-identical on and off.
  - A June experiment conditioning Cohere's decoder on a prompt ("Route B") worked once and then broke controls. It's shelved.
- **`voxtral-local` / `voxtral4b-local`:**
  - Upstream treats the 4B realtime checkpoint as a separate backend (`voxtral4b`).
  - Only the 3B splices hotwords.
- **`granite-local`:**
  - `granite`, `granite-4.1` and `granite-4.1-plus` are one code path; plus is detected from the GGUF.
  - The glob is pinned to the plain 2B (`granite-speech-4.1-2b-q*.gguf`), and `RepairSupersededDefault` fixes old configs.
  - Upstream's comments suggest the plain 2B might be on the wrong chat template, which could explain the lowercase and paraphrased output. Unverified.

---

# Part 5 — Local ASR (CrispASR)

[CrispASR](https://github.com/CrispStrobe/CrispASR) is a ggml-based, multi-backend ASR engine: one `crispasr.exe` runs Parakeet, Cohere, Qwen3-ASR, Voxtral, Granite, Whisper and more. The deployed binary's `--list-backends` shows 108. WhisperInk runs it as a local HTTP server per preset.

## 5.1 Current deployment

- **Desktop:** the prebuilt release **v0.8.30** (git `f632edf3`, built 2026-08-28), CUDA asset, deployed 2026-08-29 into `%APPDATA%\.WhisperInk\cohere-gguf\` with `scripts\update-crispasr.ps1 -Tag v0.8.30`.
  - It's a release artifact, so there are **no local patches** on the binary. The ggml-blas PkgConfig patch in the sibling clone only matters for source builds.
  - Its CUDA archs `60/61/70/75/86/89/120` include sm_86 (the 3090s and 3080).
- **Never deploy a GPU release older than v0.8.30.** Every Windows CUDA build through v0.8.29 was compiled `-march=native` on an AVX-512 runner and dies with `SIGILL` on other CPUs (upstream #374).
- **Newer releases:** v0.8.31–v0.8.36 are out (v0.8.36 on 2026-09-23). None of them changes biasing.
  - v0.8.31 added a CUDA-13 package.
  - v0.8.36 fixes a command injection in the server's ffmpeg fallback (`6409647a`). WhisperInk's WAV uploads never reach that path.
  - **Not deployed.** A/B first (5.2).
- **Laptop:** a pre-v0.7 binary. v0.7+ features (server hotwords, beam, auto-warmup) don't apply there.
- **Also running from the same exe:** the owner's own servers, **not WhisperInk's**, on ports 8001 (Qwen3) and 8880 (Kokoro TTS), pinned with `--device 1`. Leave them alone. A CrispASR update swaps the exe under them too.
- **Rollback sets** in `cohere-gguf\`: `.old-2026-08-29-1706\` (v0.7.1), plus older `.old-*` and `.v0.7.1-cuda-regressed\`.

## 5.2 Updating: prebuilt releases (the normal path)

```powershell
scripts\update-crispasr.ps1 -Tag v0.8.30                                              # CUDA asset (default)
scripts\update-crispasr.ps1 -Tag v0.8.30 -Asset crispasr-windows-x86_64-vulkan.zip   # or cpu / cpu-legacy
```

**Always pass `-Tag`. The script's default is `v0.7.1`**, below the v0.8.30 floor, so running it bare downgrades.

The script:
1. downloads the asset with `gh` into `%TEMP%`;
2. **force-stops every crispasr server running from `cohere-gguf`**. That includes the owner's own 8001 and 8880 servers, which have to be restarted by whatever started them. WhisperInk respawns its own on the next take;
3. backs up `crispasr.exe` and `*.dll` to `cohere-gguf\.old-<stamp>\`;
4. swaps in the new binaries, leaving the GGUFs alone;
5. smoke-tests `--help`. Empty output is the `STATUS_DLL_NOT_FOUND` signature and triggers an automatic restore.

Backups and temp folders are never pruned: 8 backups, ~1.15 GB, as of 2026-09-23. Keep the script **pure ASCII**: PowerShell 5.1 reads BOM-less files as ANSI.

**A/B before accepting any release.** Releases have shipped broken: #374, and v0.7.1's cohere 10× regression. Run one harness against both binaries back-to-back, in server mode, warm, on `jfk.wav` and the clinical clips, and compare medians and transcripts.

The v0.8.30 acceptance run (2026-08-29):

| Preset | Before | After | Transcripts |
|---|---|---|---|
| cohere q6_k | 381 ms | 402 ms (noise) | Identical |
| parakeet-rnnt + `fullstop` | 749 ms | **325 ms** | Identical |

Compare numbers from the same harness only: an older 317–352 ms figure came from a different one.

## 5.3 How WhisperInk runs it (`CrispAsrServerTranscriber`)

The first take on a local preset lazily spawns:

```
crispasr.exe --server --host 127.0.0.1 --port <port> -m <model> -t <min(8, cores)> -np
             [--backend <LocalBackendHint>]
             [-ng  when the effective GPU backend is cpu | --gpu-backend X  when it's a named GPU]
             [--punc-model X] [--truecase-model X]
```

**Port.** `LocalServerPort`, then the port in the URL, then 8103. Health checks prefer the `BaseUrl` port, which agrees for every shipped preset.

**Startup.**
- The effective GPU backend is the preset's `LocalGpuBackend`, else the global `CrispGpuBackend`. On the desktop that's `cuda`.
- `auto` passes nothing, and ggml's `init_best` picks CUDA > Vulkan > CPU.
- It then waits up to **120 s** for `/health`: v0.7+ warms the model at startup, and a cold CUDA load includes the VRAM upload.

**Model.** `ResolveModel` takes the **first** `Directory.EnumerateFiles(folder, LocalModelGlob)` match. The folder is `LocalModelFolder`, else `cohere-gguf`.
- A loose glob therefore silently loads the wrong model ([6.1](#61-add-a-provider)).
- The model is resolved **once per cached transcriber**. A GGUF dropped in after a failed take isn't seen until the transcriber is recreated (provider switch, settings save, GPU change or restart).

**Requests.** Each take POSTs to `/v1/audio/transcriptions` with, in order:
- `language`;
- `hotwords` (comma-joined, only when there are bias terms), plus `hotwords_boost` if `HotwordsBoost` is set;
- `beam_size` if `LocalBeamSize` is set (null = greedy, the server default since `f1b5e546`);
- `response_format=json`;
- any `LocalExtraParams`. Reserved keys are skipped: `language`, `hotwords`, `hotwords_boost`, `beam_size`, `response_format`, `file`;
- `file` last.

The OpenAI `prompt` field is not sent. The whisper backend would read it as an initial prompt, but there's no whisper preset.

**Lifecycle.**
- The server stays resident.
- Switching away drops it (`TranscriberFactory.Drop`), and `Dispose` kills the process tree.
- A request cancelled mid-inference (the take's deadline: 180 s plus the audio length) marks the server not-ready, so the next take respawns it instead of queueing behind a wedged inference.

**Failure evidence.** The transcriber keeps the last 20 lines each server printed. It logs them with the exit code when a server dies at startup or is found dead at the next take, and without one when a live server returns empty text. A `/health` timeout is logged with the output but without an exit code, and a non-2xx answer with only a body preview. Search `debug.log` for `CrispAsr(`.

## 5.4 DLLs

**Deploy every `*.dll` from the release zip or `build\bin\Release\`**, never a hand-picked subset. A missing DLL makes `crispasr.exe` exit with `-1073741515` (`0xC0000135`, `STATUS_DLL_NOT_FOUND`) before printing anything. That shows up as "`--help` prints nothing" or as empty transcripts.

| Version | DLL set |
|---|---|
| Every backup from June 2026 on, v0.7.1 and v0.8.30 included | `crispasr`, `whisper`, `ggml`, `ggml-base`, `ggml-cpu`, `ggml-cuda`, plus `cublas64_12`, `cublasLt64_12`, `cudart64_12` on CUDA |
| The April 2026 build (`.old-2026-04-18`) | Per-backend DLLs (`parakeet`, `cohere`, `canary`, …) |

"Diagnose active provider" still checks for the old per-backend names.

## 5.5 Punctuation and truecasing (server-side post-processors)

- **`LocalPuncModel`** → `--punc-model`: `fullstop`, `auto`/`firered`, `punctuate-all`, `pcs`, or a path. It restores punctuation and sentence case for backends that emit none, such as Parakeet RNNT/CTC. Server-mode support has been upstream since §166 (`36f35f2a`, PCS/CTC auto-enable `8d803f04`).
- **`LocalTruecaseModel`** → `--truecase-model`: wired, validated and **off everywhere**. On test audio `auto`/`crf` over-capitalize ("my Fellow americans ask Not what Your Country Can…") and `lstm` does nothing. Revisit when upstream's models improve.
- Speech-LLM backends (Qwen3, Voxtral, Granite) punctuate natively. Don't add a punctuation model to them.

## 5.6 Building from source (rarely needed)

`scripts\build-crispasr.ps1` does the following:
1. enters the VS developer shell;
2. clones or pulls `..\CrispASR`;
3. auto-detects CUDA and Vulkan;
4. configures with `-G "Visual Studio 17 2022" -A x64` (newer generators fail on a Build-Tools-only install) and `-DWHISPER_BUILD_TESTS=OFF` (a Catch2 FetchContent fails offline);
5. builds `--target crispasr-cli`, whose output is named `crispasr.exe`;
6. copies the exe and every DLL into `cohere-gguf\`.

**Prefer the release path.** This script:
- makes no backup, stops no servers, runs no smoke test, and leaves stale DLLs in place;
- doesn't check git failures. The clone sits at `9eecfd43` with one uncommitted patch (ggml-blas PkgConfig optional), so a failed pull quietly builds old code;
- would replace the tested v0.8.30 release with an untested build;
- has a `whisper-cli.exe` fallback branch, which is dead code.

`whisper-server` is a different, legacy target. Server mode is `crispasr.exe --server`.

**The clone** is a sibling directory rather than a submodule, so updating it is a plain `git pull`.

**Upstream documentation** is in `README.md`, `docs/server.md`, `docs/cli.md`, `PERFORMANCE.md` and `ARCHITECTURE.md`, and the clone tracks a 47-line upstream `CLAUDE.md`.

## 5.7 History that still explains the settings

- **v0.7.1 regression.** v0.7.1 made cohere ~10× slower on CUDA and 2× on CPU. The cause was a `beam_size=5` default whose beam search snapshotted the KV cache through host memory (upstream #161, fixed by `4b27392f`). Upstream `f1b5e546` then made greedy the server default, so `LocalBeamSize: null` means greedy.
- **Resampling fix.** The v0.8.30 headline fix (`ac4aa478`, non-16 kHz input aliased by linear resampling) doesn't affect WhisperInk, which records 16 kHz mono. It matters for audio fed to crispasr by hand.
- **Deferred:** WebSocket realtime dictation over `--ws-port`. It is whisper-only upstream and would need a whisper GGUF preset, a streaming float32 mic path and a partials UI.
- **Dropped:** translation, which needs Granite AST or a ~502 MB m2m100 model plus a second server.
- **Unused:** the `/load` model hot-swap endpoint.

---

# Part 6 — Recipes

## 6.1 Add a provider

Work out which of four cases you're in **before editing anything**. Only one of them needs a new class, and assuming a new provider means new code has been the recurring time sink.

| You want to add… | Work |
|---|---|
| **A.** Another GGUF that CrispASR already supports | **No new class.** A hand-written `config.json` entry (no rebuild) for one machine, or a `CreateDefaults()` entry (C#, rebuild) to ship it to every install |
| **B.** A cloud API that speaks OpenAI-style multipart | **No new class.** Same as A, with `TranscriberKind` left at `Http` |
| **C.** A cloud API with its own protocol (Deepgram, Soniox, Chirp 3, Modulate, Smallest, Reson8) | Four edits: an enum value, an `ITranscriber` class, one factory arm, a preset |
| **D.** A new *knob* on existing providers | Three **mandatory** edits ([6.2](#62-add-a-field-to-apiprovider)). Independent of A–C |

**What you never touch.** `HealthProbe` and `ProviderDiagnostics` dispatch on `TranscriberKind`, never on the provider id, so new providers are picked up automatically. `InferKindFromLegacyId` only matters for hand-written `config.json` entries with no `TranscriberKind`, since `CreateDefaults()` always writes one. Listing a new id there is harmless insurance.

**A new preset beside an existing one** (another model on a service the user already has) needs no key step. On first merge, `ApiProvider.InheritFromSibling` copies the API key from a same-kind, same-host provider, preferring the one whose id prefixes the new id. Between ElevenLabs entries it also copies `ScribeKeytermsRaw`, `TagAudioEvents` and `NoVerbatim`. The values are copied, not linked.

### A. Another CrispASR GGUF

1. **Check the deployed binary has the backend.** Run `& "$env:APPDATA\.WhisperInk\cohere-gguf\crispasr.exe" --list-backends`. A name in the left column runs today; a name only in upstream's docs doesn't.
2. Put the GGUF in `%APPDATA%\.WhisperInk\cohere-gguf\`, or in a sibling folder and set `LocalModelFolder`.
3. Pick the next free port from the table below and add the entry:
   ```json
   { "Id": "canary-local", "Name": "Canary Local (CrispASR)",
     "BaseUrl": "http://localhost:8113", "TranscriptionEndpoint": "http://localhost:8113/v1/audio/transcriptions",
     "TranscriberKind": "LocalCrispAsrServer", "LocalServerPort": 8113,
     "LocalModelGlob": "canary-1b-*.gguf", "LocalBackendHint": "canary",
     "BiasMechanism": "none", "Language": "en" }
   ```
4. Restart WhisperInk. The server spawns on the first take.
5. **Verify by hand first**, using the exact command WhisperInk will run, so any failure belongs to the model and not the plumbing:
   ```bash
   crispasr.exe --server --host 127.0.0.1 --port <PORT> -m <MODEL.gguf> -t 8 -np --backend <HINT> --gpu-backend cuda
   curl -s http://127.0.0.1:<PORT>/health
   curl -s -F "file=@jfk.wav" http://127.0.0.1:<PORT>/v1/audio/transcriptions
   curl -s -F "hotwords=termA,termB" -F "file=@clip.wav" http://127.0.0.1:<PORT>/v1/audio/transcriptions
   ```
   Then run the six clips through `_scratch\biasing\_local_bias_ab.ps1`, after adding the model to its `$runs`. Sample audio lives in `..\CrispASR\samples\jfk.wav`.

**The fields that bite:**
- **`LocalModelGlob`: pin it to the model, not the family.** Presets share one folder, and the first `EnumerateFiles` match wins, so this fails *silently* and looks like poor model quality. `parakeet-*` matches both Parakeet GGUFs; `granite-speech-*` loaded 2b-plus for four months. When a shipped glob turns out wrong, also add a line to `ApiProvider.RepairSupersededDefault` ([6.3](#63-change-a-shipped-default)).
- **`LocalBackendHint`**: needed when GGUF metadata doesn't auto-detect: `cohere`, `voxtral`, `voxtral4b`, `granite`. Pin it anyway when unsure; it costs nothing.
- **`BiasMechanism`** is **informational for local presets**: the transcriber sends `hotwords` whenever the shared list is non-empty, whatever this says. Use `hotwords` for a backend that reads the terms. Every shipped local preset says `hotwords`, including Cohere and Voxtral 4B, whose backends ignore them, so the label doesn't tell you whether biasing works; the table in [4.3](#43-context-biasing) does. **Measure before trusting a no-op claim**: Granite's prompt splice went unnoticed for three months.
- **`LocalPuncModel`**: only for backends with no native punctuation (Parakeet RNNT/CTC → `fullstop`). Leave `LocalTruecaseModel` unset.
- **`LocalGpuBackend`**: blank inherits the global setting. Pin `cpu` only for a deliberate CPU-fallback preset.

**Ports.** Each local preset owns one. **Next free: 8113.**

| Port | Preset |
|---|---|
| 8103 | `parakeet-local` |
| 8105 | `cohere-local-q6k` |
| 8106 | `voxtral-local` |
| 8107 | `granite-local` |
| 8108 | `voxtral4b-local` |
| 8109 | `parakeet-rnnt-local` |
| 8112 | `qwen3-asr-1.7b-local` |
| 8766 | `cohere-gguf-server` |
| 8001, 8880 | The owner's own crispasr servers. **Not WhisperInk's** |

Retired: 8102 (the old hand-run `qwen3-asr` Http preset), 8104/8767/8768 (the Q4, CUDA and CUDA-Q8 Cohere presets), 8110 (was the Canary example), 8111 (the LFM2-audio trial). The retired Cohere ids still resolve in `InferKindFromLegacyId`, so an old config entry without a `TranscriberKind` keeps working. `qwen3-asr` falls through to `Http`, which is what it was; `lfm2-audio-local` isn't listed.

**Removing a preset for real** means deleting it from `CreateDefaults()` *and* from `config.json`. The additive default-merge re-adds any shipped default the config lacks, on every launch.

### B. Cloud API on OpenAI-style multipart

As A, with `TranscriberKind: "Http"`, plus:
- `BaseUrl`, and `TranscriptionEndpoint` if it isn't `/v1/audio/transcriptions`;
- `AuthHeaderName` (blank = Bearer);
- `ModelFieldName` (`model` or `model_id`);
- `TranscriptionModel`.

`HttpTranscriber` always sends string fields before the file part, because Cohere v2 rejects the other order. Assume any new API might too.

### C. Cloud API with its own protocol

Only when the API isn't OpenAI-shaped (a raw-body POST, query-param options, a job/poll cycle, non-Bearer auth, or a nested response). Four edits:

1. A `TranscriberKind` value in `AppConfig.cs`.
2. `<Name>Transcriber.cs` implementing `ITranscriber.TranscribeAsync(byte[], IReadOnlyList<string>, CancellationToken)`. Copy the closest existing one:
   - `DeepgramTranscriber`, `SmallestTranscriber` or `Reson8Transcriber` for a one-shot raw body with query params;
   - `ModulateTranscriber` for one-shot multipart where the endpoint picks the model;
   - `SonioxTranscriber` for an async job.
3. One arm in `TranscriberFactory.Create`. Pass the shared `HttpClient` so the pool, keep-alive and pre-warm apply.
4. A preset in `CreateDefaults()`.

**Rules for the transcriber:**
- Route bias terms to the API's native field inside the transcriber, and make `BiasMechanism` a descriptive string.
- Pass the `ct` you're given to **every** request. It is the take's deadline and the only thing that ends a slow call; the client timeout is a 2 h 10 min backstop.
- Give the cancel path two arms:
  - `catch (OperationCanceledException) when (ct.IsCancellationRequested)`, logged as "stopped at the take's deadline";
  - a plain `TaskCanceledException` catch, for the HttpClient's own timeout.
- A request that must outlive a cancelled take, such as cleanup (Soniox's DELETEs), gets its own short `CancellationTokenSource`.

**Probe the live API before trusting its docs.** Smallest.ai's docs listed 26 of 46 language codes and never mentioned region-gated 400s. Send real audio with edge values (the house `auto` sentinel, a wrong model, a bad key) and read the real error bodies.

## 6.2 Add a field to `ApiProvider`

Three places, all mandatory. Miss one and the field misbehaves silently:

1. **`AppConfig.cs`**: the property and its default.
2. **`MainWindow.LoadConfig`**: an explicit `TryGetProperty("<Name>", …)` block. Without it the field never loads from `config.json`. `LocalPuncModel` looked like it worked for weeks only because its default happened to match.
3. **`ProviderSettingsWindow.CloneProvider`**: one assignment. The dialog edits a clone, so a missing line means **opening the settings dialog silently resets the field**. Deep-copy dictionaries and lists.

```bash
grep -c 'TryGetProperty("YourField"' MainWindow.xaml.cs
grep -cE "YourField\s*=\s*src\.YourField" ProviderSettingsWindow.xaml.cs   # CloneProvider aligns its = signs
```

**If the field should come back when a user blanks it** on a shipped preset, add it to the backfill loop in `LoadConfig` too. Nine fields are backfilled today.

Computed properties (the `Resolved*` family, `IsElevenLabs`, `RequiresApiKey`) and static helpers need none of this. Mark any new computed property `[JsonIgnore]`: the existing nine are serialized as noise ([10.2](#102-known-bugs-found-in-the-2026-09-23-audit)).

## 6.3 Change a shipped default

`LoadConfig` adds presets missing from a config and backfills *blank* `Local*` and `BiasMechanism` fields. It **never overwrites** a value a config already has. So changing a default in `CreateDefaults()` only reaches new installs and new presets.

To fix a value that is already sitting in existing configs, add a case to `ApiProvider.RepairSupersededDefault`. It must match the *exact old shipped value*, so a user's own edit is left alone. Log what changed and add a harness check. The Granite glob is the example.

## 6.4 Add a config knob (root level)

Follow `StreamUpload` or `ClipboardRestoreMs`, the only root knobs read with a `ValueKind` guard. `WarmMicEnabled` and the numeric knobs are read unguarded, which is how one wrong-typed value aborts the load. A new knob needs:
- a field with a default and a comment explaining why the default is what it is;
- a guarded read in `LoadConfig`: check `ValueKind` before `GetBoolean()`/`GetInt32()`, and clamp numbers. An exception there aborts the rest of the load and can end with the API keys overwritten (the warning in [3.2](#32-configuration));
- a line in `SaveConfig`'s anonymous object.

Then list it under the tuning knobs in [3.2](#32-configuration).

## 6.5 Add a menu item

Add one `MenuNode` in `MainWindow.BuildAppMenu()`. Both the tray (WinForms) and the bar's right-click menu (WPF) render the same tree, so the item appears in both. Use `MenuSurface.TrayOnly` or `BarOnly` only when an item makes no sense on the other surface. The tray menu is rebuilt on every open, so labels can show live state.

## 6.6 Touch the press or stop path safely

Before merging, check:
- **Does any exit path lose the WAV?** Every early return after `UnsentTakes.Begin` must `Delivered`/`Keep` the take. The `catch` keeps unresolved takes.
- **Is anything new slow or blocking on the UI thread during the press?** The pre-roll only covers ~400 ms of delay before `BeginCapture`.
- **Does every failure get Error or Warn plus a log line?** Does `Success` still mean "all of it, pasted where you were typing"?
- **Is the target window still captured at the press**, and delivery still verified?
- **Does the `finally` still release modifiers and reset `_recState`?** A stuck Ctrl or a dead hotkey is the cost of forgetting.
- **Any change to `MicCapture`:** the sink and the WAV must get identical bytes in the same order under `_gate`. The streamed upload's byte-count check depends on it.

Then run the harness, deploy ([1.4](#14-build-test-deploy)), and ask the owner to try:
- a tap;
- a silent hold;
- a normal take;
- a take after clicking into another window (expect "Copied — paste it");
- a long take (over 60 s).

`debug.log` should tell the story of each.

## 6.7 Measure latency or accuracy

- **Cloud latency:** `_scratch\scribe-latency` ([7.2](#72-scribe-latency-the-live-elevenlabs-probe)). Interleave variants within one run and compare medians, never numbers across days: server variance is ±150 ms. Every run bills real audio.
- **Accuracy:** the six clips ([4.3](#43-context-biasing)) through the provider, with and without the bias list. For a local model, use `_scratch\biasing\_local_bias_ab.ps1`.
- **Real use:** `debug.log`'s `took …ms` and `Batch pipeline` lines over days. Streamed takes say `after release (streamed)`.
- Record every result in this file with the date, method and n.

## 6.8 Update CrispASR

See [5.2](#52-updating-prebuilt-releases-the-normal-path): A/B first, then the script. The owner's other servers on 8001 and 8880 use the same exe.

---

# Part 7 — Testing

The harnesses compile the **shipping source files directly** (each csproj `Compile Include`s them), so they test the real code, not a copy. When you add a file a harness uses, add it to that csproj.

## 7.1 crisp-harness: the regression suite

```powershell
cd _scratch\crisp-harness
.\make-speech.ps1               # once per machine: writes speech.wav (TTS). It's git-ignored, and every run reads it, even `fast`
dotnet run -c Release -- fast   # 110 checks, ~30 s, no API calls, no crispasr
dotnet run -c Release           # full: ~123 checks, adds the real crispasr.exe on CPU and a 16 s slow-server check
```

| Section | Checks | Covers |
|---|---|---|
| 0a | 9 | `TranscriptionDeadline` values and caps |
| 0b | 7 | ElevenLabs transcript cleanup |
| 0c | 11 | `TranscriptCoverage` on synthetic WAVs |
| 0d | 12 | `UnsentTakes`: deliver, keep, crash recovery, orphan WAV, retention, ordering |
| 0e | 18 | Provider resolution (ElevenLabs auth, model field, bias), `InheritFromSibling`, the Granite glob and its repair |
| 0f | 12 | `SpeechDetector` on synthetic audio over a noise floor. Dropped as silent: the 2026-09-23 silent take, a fan, clicks. Sent: quiet speech at that evening's level (RMS under 0.003), a cold-mic take with no pre-roll, a short phrase in a 30 s hold, and loud steady noise. Also fail-open, digital zero, and a disabled gate |
| 0g | 4 | Takes judged silent: only the newest 5 kept, never pushing out a real failure, and left out of the startup count |
| 1 | 21 (+1 full) | `HttpTranscriber` against a fake server on `127.0.0.1:18999`: the ElevenLabs field set and order, keyterm merging and validation, word timing, `auto` language, a non-ElevenLabs provider getting no Scribe fields, the deadline |
| 1b | 16 | `StreamedTranscription` against the fake server: chunked, byte-exact PCM, field parity, and the short-stream / discarded-take / HTTP-error / deadline / 5-minute paths |
| 2a–2d | ~13 (full only) | Real `crispasr.exe` on CPU (ports 18997/18998, needs a `parakeet-tdt-*.gguf`): startup failure, empty text, deadline mid-inference then restart, server killed between takes |

The fake server runs on loopback, so providers pointed at it get the **local** deadline budget. Use a cloud provider object for cloud budgets.

## 7.2 scribe-latency: the live ElevenLabs probe

`dotnet run -c Release -- <mode> [rounds|gapSeconds] [iterations]`. It reads the `elevenlabs` key and the bias list from `config.json`. **Every mode bills real audio**; with more than 100 keyterms, each request bills at least 20 s. Audio comes from `..\CrispASR\samples\jfk.wav` and `crisp-harness\speech.wav`, plus the clinical clips in `medical` mode.

| Mode | Measures |
|---|---|
| `variants` | The shipped request vs no keyterms vs `pcm_s16le_16` (warm) |
| `conn` | Warm vs cold vs pre-warmed connection |
| `idle <sec> [n]` | Does a pooled connection survive an idle gap? |
| `stream` | Today's upload vs a probe-built streamed upload |
| `medical` | `scribe_v2` vs `scribe_v2_medical` latency, then both ± keyterms on the clinical clips |
| `medcheck` | Does Medical return word timing? |
| `preset` | The shipped `elevenlabs-medical`, keyed via `InheritFromSibling`, one request |
| `shipstream` | The **shipping** `StreamedTranscription` vs pre-warm + POST, then whether a cancelled stream is billed (reads the account's usage counter) |

## 7.3 Other tools

| Tool | What | Cost |
|---|---|---|
| `_scratch\reson8\` (`dotnet run`) | Reson8 wire-format probe against a local listener (`127.0.0.1:8899`): 53 checks | Free |
| `_scratch\reson8\run-clips.ps1` | The six clips × (no phrases, 3 terms, the full list) against the live API; CSV in `results\` | **Paid** |
| `_scratch\reson8\setup-reson8.ps1` | Installs a Reson8 key. **It stops WhisperInk, rewrites config.json (with a backup), runs `run-clips`, and relaunches `_publish`** | **Paid** |
| `_scratch\biasing\_local_bias_ab.ps1` | Granite (2b and 2b-plus) and Qwen3 × clips × no bias / the real list. Own servers on ports 18207–18212. PowerShell 7, CUDA | Free (GPU) |
| `_scratch\biasing\_hotwords_ab.ps1`, `_boost_sweep.ps1`, `_cohere_baseline.ps1` | June's Cohere/Parakeet hotword and boost experiments (ports 8201–8213) | Free |
| `_scratch\biasing\_routeb_gate.ps1` | The shelved "Route B" test. Needs a patched binary at a hardcoded path | Obsolete |

**Clips** (`_scratch\biasing\clips\`, git-ignored): the owner's own voice, recorded from `RECORD_THESE.md`. Never substitute TTS for accuracy tests: the misses are speaker- and acoustics-specific. `RECORD_THESE.md` and the June scripts still call `ureterolithiasis` a control; it has since proved to be a hard term.

## 7.4 What no test covers — check by hand

- **`MicCapture` with a real device**, including `PreRollRing` ordering. The "400 randomised trials" verification quoted in older notes was a one-off and **isn't in the repo**; adding it to the harness is on the backlog.
- **The keyboard hook**, suppression and the watchdog.
- **The clipboard restore** (`TextInjector`: the delay and the sequence-number guard). A harness run would clobber the real clipboard.
- **`SpeechDetector` on real silence.** Its silent cases are synthetic; the takes kept under 🔇 Judged silent are the real-world check.
- **WPF UI**: menus, dialogs, the bar, balloons, sounds.
- **The whole press → paste path** in the running app.

After touching any of these, deploy ([1.4](#14-build-test-deploy)) and have the owner try a tap, a silent hold, a normal take, a take after clicking into another window (expect "Copied — paste it"), a take over 60 s, and a take right after switching providers. Then read `debug.log` for each.

---

# Part 8 — Troubleshooting and gotchas

## 8.1 Running the app

- **Closing the bar doesn't quit.** It hides to the tray and the keyboard hook stays live (`Closing` is intercepted). Only ❌ Quit exits, or closing when `QuitOnClose` is on. The hook lives on the main window's thread, so a real exit ends dictation.
- **Two copies answer Ctrl+Space.** `_publish\WhisperInk.exe` plus an old test build (`%USERPROFILE%\WhisperInk-step0\` or `-step1\`) means double recordings and pastes. Run one. `Get-Process WhisperInk` shows the count; `MainWindowHandle = 0` is normal for this borderless, taskbar-less window.
- **Another app owns Ctrl+Space.** PowerToys Peek did (2026-08-28, rebound to Ctrl+Shift+Space). Symptoms: dead or erratic hotkey and `[hook-watchdog]` reinstall lines.
- **The mic is chosen by index.** The 🎙 menu re-lists devices each time it opens, but the setting stores an *index*, and plugging or unplugging a device can shift it onto a different mic. Re-pick after device changes.
- **A stuck modifier** should clear through `ReleaseAllModifierKeys()` after every take. If one persists, tap that key once, and capture `debug.log` around it.
- **The first take after 180 s idle has no pre-roll.** The warm mic is released (`WarmMicIdleSeconds`) and reopening costs ~130 ms, which can clip the first syllable. That's the trade for the Windows mic-in-use indicator; set it to 0 to hold the mic while running.
- **Slow CPU inference on the laptop is usually the power plan.** Balanced pins the Ryzen at base clock even when plugged in, roughly halving ASR speed. Use Ultimate Performance:
  ```powershell
  powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61
  powercfg /setactive 5898ace7-acb8-479d-b9c1-54af0f151d1b
  ```
  The symptom: RTFx 1–1.5× where 2–3× is expected.
- **The .NET 8 *Desktop* Runtime** is needed for framework-dependent builds. The self-contained `_publish` build carries its own.

## 8.2 Dictations

- **"Where did that dictation go?"** Check ↻ Unsent dictations (not delivered, with the reason), then History (delivered, including clipboard-only), then `debug.log` around that time. `[unsent] kept …` gives the reason, with the transcriber's `HTTP` or error line just before it. A failed request logs no `[error]`; a deadline, no text for real sound, or a mic failure does.
- **Every take fails with 401 on a hand-added provider.** Check the `Active provider: … (auth=…)` log line. ElevenLabs hosts now resolve a blank header to `xi-api-key`; any other service needs the right `AuthHeaderName` typed in.
- **A take vanished with a quiet blip, or "Too quiet? Saved ↻".** The silence gate judged it silent. It's under ↻ Unsent → 🔇 Judged silent (the newest 5), ready to retry. If real speech keeps landing there, compare the `[skip]` line's RMS, floor and speech values with [3.4](#34-capture-pipeline-miccapturecs).
- **Your old clipboard was pasted instead of the dictation.** The app handled the Ctrl+V after WhisperInk had put the old clipboard back. Raise `ClipboardRestoreMs` ([3.7](#37-delivery-textinjectorcs-mainwindowdeliverasync)). The dictation itself is in History.
- **"Copied — paste it" when you didn't expect it.** The window the take started in wasn't in front at delivery: you clicked elsewhere, or Windows refused the focus switch. The text is on the clipboard. This is by design.
- **"No text!" on a take with real speech.** The backend answered with nothing. For local models that's almost always a server problem (see 8.3). For cloud, look at the `HTTP` line.
- **A local preset transcribes badly or with the wrong model.** Check which GGUF `LocalModelGlob` actually matches (the first `EnumerateFiles` hit wins). See [6.1](#61-add-a-provider).
- **`debug.log` is per session.** The previous one is `debug.previous.log`, and a second restart overwrites it, so grab a support bundle before restarting after a failure. Timestamps are time-only; the header line has the date.

## 8.3 Local ASR

- **A silent exit or empty transcripts means a missing DLL.** Exit code `-1073741515`. Re-run `scripts\update-crispasr.ps1`, which deploys every DLL and restores on a failed smoke test. For source builds, copy all `*.dll`.
- **The first take on a local preset takes seconds.** It spawns the server and loads the model: 1.5–3 s when warm, up to 120 s for a cold CUDA load. Switching providers drops the old server.
- **Search `CrispAsr(` in `debug.log`.** What each failure logs:
  - a server death: the server's last 20 output lines and the exit code;
  - an empty result: the last output lines, without an exit code (the server is still running);
  - a missed health check: the output, without an exit code;
  - a non-2xx answer: a body preview.

## 8.4 Config and settings

- **`config.json` is rewritten from memory** on every save (provider switch, settings dialog, menu toggles). Edit it by hand only while WhisperInk is **not** running, back it up first (`config.json.bak-<stamp>`), and match the existing JSON types. A wrong-typed value can make the next launch **overwrite the file and drop the API keys** ([3.2](#32-configuration)).
- **`*ExtraParams` values must be strings**: `{"vad": "true"}`, not `{"vad": true}`. Other types are silently dropped.
- **Saving the settings dialog normalizes some fields** on every provider shown in it that session. A language of `auto` or `nl,en` becomes `en`, and a trailing `/` is stripped from URLs, which breaks Smallest.ai's endpoint. Fix those in config.json afterwards.
- **A keyless server elsewhere on the LAN gets "No API key!".** Only `localhost`, `127.0.0.1` and `[::1]` count as local. Give it any placeholder key.
- **A provider field resets every time the settings dialog opens.** `ProviderSettingsWindow.CloneProvider` is missing that field. A field that never loads at all is missing its `LoadConfig` block ([6.2](#62-add-a-field-to-apiprovider)).
- **A deleted default provider keeps coming back.** Delete it from `CreateDefaults()` too.
- **A changed default doesn't reach existing configs.** Use `RepairSupersededDefault` ([6.3](#63-change-a-shipped-default)).
- **`LoadConfig` catches all exceptions.** A malformed value can abort the load and leave defaults in memory, and `Config error: …` in the log is the only sign. Guard new reads by `ValueKind`.

## 8.5 Building and tooling

- **`UseWindowsForms` alongside `UseWPF`** adds global usings that collide (`Application`, `MessageBox`, `Clipboard`, `Color`). The csproj removes them, including in a `BeforeCompile` target for the generated `wpftmp` project. Tray code uses fully qualified WinForms names.
- **The version stamp:** the csproj writes `git rev-parse --short HEAD` into `SourceRevisionId`, and the SDK appends it (`1.0.0+<hash>`). Setting `InformationalVersion` by hand double-appends.
- **In single-file publish `Assembly.Location` is empty.** Use `Process.GetCurrentProcess().MainModule.FileName` or `AppContext.BaseDirectory`.
- **Never run `publish.ps1` while WhisperInk runs**: it deletes `_publish\` first. Use the staged deploy in [1.4](#14-build-test-deploy).
- **An XML comment can't contain `--`.** It broke a csproj once.
- **`_scratch` has its own projects.** The app csproj excludes `_scratch\**` from compile, or a harness `Program.cs` gets compiled into the app (a confusing duplicate-attribute error in a `_wpftmp` project).
- **WAV headers aren't always 44 bytes.** `System.Speech` and NAudio write an 18-byte fmt chunk, making a 46-byte header. Find the `data` chunk (`StreamedTranscription.PcmLength` does).
- **Fake-server tests run on loopback**, so a provider pointed at them gets the *local* deadline budget. Use a cloud provider object when testing cloud budgets.
- **`.claude\worktrees\` holds old session worktrees** with stale copies of the source. Exclude `.claude` from repo-wide searches, or they'll show old code.
- **Line endings:** the working copies are CRLF. The Edit tool preserves them; `sed -i` inserts can create LF lines. Git normalizes the diff, but keep files consistent.
- **This machine** has no Python. It has Node 24, PowerShell 7 (`pwsh`) and Git Bash. `gh` is authenticated for `praxeo`.
- **Unrelated but recorded:** the Home Assistant `update_automation` MCP tool is broken on the owner's instance. Edit its YAML by hand.

---

# Part 9 — History and decisions

## 9.1 Timeline

All 59 commits (as of `8c0a52d`) are linear on `main`; feature branches are fast-forwarded.

| Date | Commit(s) | What changed |
|---|---|---|
| 2026-04-09 | `5c3cf80`, `07bf155` | First app: ONNX Cohere, a prompt window, a FastAPI Cohere server (`server\`) |
| 04-18 → 04-20 | `9a98afd`, `a0d491f`, `ba65d25`, `e1dc9b8`, `30f9fde`, `81a1798` | CrispASR GGUF transcribers, Scribe keyterms, the Parakeet preset, **additive merging of new defaults**, auto-spawned local servers, Cohere Q4/Q6_K |
| 04-20 | `8a0836d`, `159f2af`, `a4fdf81`, `b26a179`, `3c11ab1` | Git-hash version stamping, install scripts, tray + health probe + support bundle + diagnose, the GPU-backend setting, Voxtral local |
| 05-03 | `52ecb1d` | Granite Speech 4.1 local |
| 05-12 → 05-18 | `fcd39f2`, `cceb2a6` | Scribe `tag_audio_events`/`no_verbatim`; clipboard preserved across dictations |
| 05-26 → 05-27 | `68a7318`, `5f66c01` | Google Chirp 3; **one `ITranscriber` + factory** (four Cohere classes deleted) |
| 06-09 | `677ae86`, `626022f`, `5d00de0`, `bf3ad49` | `update-crispasr.ps1`, server `hotwords`/`beam_size` (v0.7), **one menu tree for tray and bar**, hook and injector extracted, `Interlocked` state |
| 06-12 | `558a8e2`, `fcdde88`, `c78ecef`, `106455d` | Hotword A/B tests (Cohere is a no-op, Parakeet weak); Parakeet RNNT + server punctuation; Soniox |
| 06-13 | `c94786a`, `ae223a9`, `7dc52bc`, `0774501` | **Realtime, AI-edit and med-correction removed**; **one shared bias list, routed natively**; Parakeet boost not on by default; truecase and extra-params fields |
| 06-14 | `6e1fa03`, `e9ccd50` | Deepgram (general + Medical); ONNX Cohere removed |
| 08-28 | `a721ce9` | Keyboard-hook watchdog |
| 08-29 | `176d4a6`, `b0476ca` | **Warm mic + pre-roll**, persistent audio out, no mis-press lockout; Reson8, Modulate, Smallest.ai; the Qwen3-ASR 1.7B local preset |
| 09-23 | `fef3ba2`, `a16352c` | **Never lose a dictation**: deadlines, the journal, loud failures, ElevenLabs request parity (ported from elevenlabs-web) |
| 09-23 | `a9e288a` | **Scribe Medical preset**, ElevenLabs auth resolution, sibling key inheritance, the Granite glob fix, a 9-min pool + keep-alive + pre-warm, **the streamed upload** |
| 09-23 | `8c0a52d` | This file rewritten and audited against the code |
| 09-23 | the commit after `8c0a52d` | **Quiet speech no longer dropped as silence** (`SpeechDetector`), takes judged silent kept, a 1 s clipboard restore that never clobbers a newer copy. The desktop ran `SilenceThreshold` 0.001 for a few hours before it, as a stopgap |

## 9.2 Decided against: don't re-propose without new evidence

| Idea | When | Why not |
|---|---|---|
| Realtime/partials dictation mode | Removed 06-13 (`c94786a`) | Complexity for no clinical benefit at the time. CrispASR's `--ws-port` (whisper-only) is the path if it returns |
| LLM "AI-edit" or medical-correction post-processing | Removed 06-13 | Rewrites clinical text unpredictably, and it caused problems. A *deterministic* rule would still need the owner's sign-off |
| Parakeet `HotwordsBoost` on by default | Reverted 06-13 (`7dc52bc`) | Boost 10 with a real list garbled neighbouring words |
| Cohere decoder prompt-conditioning ("Route B") | Shelved 06-14 | Fixed one clip, broke another and a control |
| LFM2-Audio local preset | Trialled and removed 06-14 | CUDA flash-attention crash; CPU-only was 2.4× realtime with double periods |
| ONNX Cohere provider | Removed 06-14 | Superseded by CrispASR GGUF |
| Modulate Velma 2 as the default | Rejected 08-29 | A 6× latency variance spike, 0/5 first-word capitalization, and English Fast discards bias terms |
| Smallest.ai for clinical use | 08-29 | No biasing; Pulse Pro wrote *hematemesis* for *hematochezia* |
| Truecasing (`LocalTruecaseModel`) | Wired 06-13, off | Upstream models over-capitalize or do nothing |
| Scribe v2 Realtime | 09-23 | Keyterms ≤50 terms of ≤20 chars, less accurate than batch per ElevenLabs, costs more, no Medical variant |
| `file_format=pcm_s16le_16` without streaming | 09-23 | Measured no gain |
| A regional ElevenLabs host | 09-23 | Global routing already lands in `us-central1` |
| Granite as a biased local option | 09-23 | Biasing rewrote a correct term; lowercase output |

---

# Part 10 — Roadmap and open questions

## 10.1 Waiting on the owner's call

- **Scribe Medical's punctuation.** It drops final periods more often than Scribe v2. The options:
  - accept it;
  - switch back to `elevenlabs`;
  - add a deterministic "end with a period" rule. That's post-processing, so ask first.
- **The keyterm list.** 249 terms bill every take as at least 20 s, and Medical got the clinical clips right with none. Trim it below 100 (keep names, brands and wound-care products) after a few days on Medical? The shared list also has a typo, "Unslolth".
- **Transcripts at rest.**
  - They sit in `debug.log` (`result:` and HTTP-preview lines), `history.json`, `unsent\`, the Desktop support bundle, and `MyRecordings\temp_audio.wav`. The last two are OneDrive-synced.
  - The options: a "no transcripts in the log" switch, redaction at bundle time, and moving the debug WAV out of OneDrive.
- **A local model as the primary?** `qwen3-asr-1.7b-local` is the candidate. Measure it against ElevenLabs on real recordings first.
- **Phone link**: WhisperInk as a listener on elevenlabs-web's existing worker endpoints. **CapsLock-hold** as a second hotkey?
- **Public-repo hygiene.**
  - `plans/packaging-polish-prompt.md` contains the owner's full name in a path, and `_scratch/biasing/_routeb_gate.ps1` a hardcoded username.
  - Fixing the files is easy. Removing them from history needs a rewrite and a force-push, and that's the owner's decision.

## 10.2 Known bugs found in the 2026-09-23 audit

Each was inferred from reading the code; none was reproduced. Line numbers are at `a9e288a`.

| Bug | Where | Effect |
|---|---|---|
| **A bad config.json edit wipes the API keys** | `LoadConfig` (one try/catch; `WarmMicEnabled` and the numeric knobs are read without a type check) then `RunFirstRunCheck` → `SaveConfig` | A wrong-typed value aborts the load partway; the first-run check then saves the partial or default providers over the file |
| **The support bundle can leak API keys** | `SupportBundle.cs:194–197` | If config.json doesn't parse, it's zipped raw |
| **Google Chirp 3 ignores the take's deadline** | `GoogleChirp3Transcriber.cs:79–96, 192, 196` | Its own 30 s client limit applies instead: the hidden length limit the deadlines were meant to remove |
| **Empty text is reported as a request failure by 6 providers** | Soniox, Deepgram, Google Chirp 3, Modulate, Smallest and Reson8 return `null` for an empty transcript | "Failed — saved ↻" with no `returned NO TEXT` line, instead of "No text!", which hides how a broken backend presents. With `SilenceThreshold` at 0 it also turns a silent take's Dismissed blip into an Error. Several of their comments claim otherwise |
| **"⚡ Instant start: OFF" keeps the mic open anyway** | `RestartMicIdleTimer` returns early when warm-mic is off (MainWindow ~1287) | After the first take the mic stays open for the whole session |
| **Possible stuck Ctrl** | `StartBatchDictation`'s "No API key!" and busy returns, and the retry's `finally` | Suppression has swallowed the physical Ctrl key-up, and neither path calls `ReleaseAllModifierKeys` |
| **"No API key!" is silent** | MainWindow ~856 | No tone and no log line, which breaks "fail loudly" |
| **The watchdog false-reinstalls after quiet periods** | `KeyboardHookService.cs:139` | The sweep is skipped while the hook sees keys, so their "pressed since" bits survive and trip the next idle check (6 reinstalls on 2026-09-23 after 37–56 s of quiet). Harmless, but noisy |
| **The settings dialog breaks Smallest.ai and `auto`** | `ProviderSettingsWindow.xaml.cs:67–72, 106–108, 121` | Strips the required trailing `/` and saves `auto` or multi-code languages as `en`, on every provider shown in the dialog |
| **`update-crispasr.ps1` with no `-Tag` downgrades** | Its default is `v0.7.1` | Below the v0.8.30 floor. Its `.old-*` backups (~1.15 GB) and temp folders are never pruned |
| **A GGUF added after a failed take isn't found** | `CrispAsrServerTranscriber` resolves the glob once | Needs a provider switch, settings save or restart |
| **`cohere-gguf-server`'s loose glob** | `cohere-transcribe-*.gguf` | Downloading the q4 or q5 model (`scripts\download-cohere-*`) would silently change the CPU preset's model |
| **Stale diagnostics** | `ProviderDiagnostics.cs:77`, `CrispGpuProbe.cs:10–12` | Checks for `cohere.dll`/`parakeet.dll`; the GPU probe's comment claims it runs crispasr |
| **Computed properties saved to config.json** | `ApiProvider` has no `[JsonIgnore]` | Nine dead fields per provider (`IsElevenLabs`, `Resolved*`, …) |
| **`HttpTranscriber` has no separate client-timeout arm** | `HttpTranscriber.cs:87–97` | A client timeout logs as a generic `TaskCanceledException` (only reachable after the 2 h 10 min backstop) |
| **Dead code** | `TextInjector.TypeTextTo`/`GetSelectedText`; MainWindow `ActiveApiKey`, `TryParsePortFromUrl`, `GetWavDurationMs(string)`, private `IsLocalProvider`; `HealthProbe.Last`; the `AppConfig` class | Delete |
| **Stale comments and labels** | `AppConfig.cs` Parakeet RNNT (says null = beam-5); the `TextInjector` class comment (realtime/AI modes); comments calling it a "hook thread"; `BiasMechanism = "hotwords"` on the Cohere and Voxtral 4B presets, whose backends ignore it | Mislead readers |

## 10.3 Backlog

- **Streamed upload for Deepgram and Smallest.ai.** Both take the audio as the raw request body, with the format in query params.
- **Coverage checks for Deepgram and Soniox.** Deepgram has per-word `end` times and Soniox tokens have `end_ms`, so each is a small `ITranscriptCoverage` implementation.
- **A `PreRollRing` test in the harness.** The old "400 trials" check was never committed.
- **`TranscriptCoverage` still uses a fixed 0.01 speech-frame threshold**, from the same wrong calibration the silence gate had. On quiet takes it under-detects speech, so the incomplete-transcript check goes lenient. Switch it to `SpeechDetector`'s per-take floor.
- **Check `SpeechDetector` against real silence.** Its negative cases are synthetic. The takes kept under 🔇 Judged silent, with the levels in their `[skip]` lines, are the data.
- **`LoadConfig` should save after merging or repairing defaults.** Also make it robust per field: skip a bad field rather than abort the load.
- **Deploy CrispASR v0.8.36**, after an A/B.
- **Try a Whisper large-v3-turbo preset** for `prompt` biasing. The transcriber would need to send `prompt`, and the GGUF is ~1.6 GB.
- **Pull the batch pipeline out of `MainWindow`** into its own state machine.
- **A `-dev N` GPU-index knob.**
- **CrispASR `/load` hot-swap** instead of one port per preset.
- **Update `README.md`.** It describes deleted Cohere classes and a removed "mode = Batch", says Granite ignores hotwords, and its provider and file tables are months out of date. Also update `docs/TRANSCRIPTION_ACCURACY_GUIDE.md`, which has no Scribe Medical or streaming. There is also no `LICENSE` file, though README links to one.
- **Housekeeping.**
  - Delete the merged remote branches `feat/reson8-provider` and `fix/step0-reliability`. `origin/parameter-optimization` is an abandoned April fork.
  - Old worktrees under `.claude\worktrees\` hold stale copies of the source. **Exclude `.claude` from repo-wide searches.**

---

# Maintaining this file

This file is how the next session picks the project up, so a stale sentence here costs more than a missing one. The 2026-09-23 rewrite audited every checkable claim against the code, and found several that had quietly stopped being true:
- Granite "ignoring" bias terms;
- Reson8 "never tested";
- closing the window "uninstalling" the hook;
- a build target and a DLL history that were both wrong;
- a Chirp 3 logging gotcha that had long been fixed.

- **Update it in the same commit as the change it describes.** New behaviour goes in the matching Part. Don't append dated narrative at the end.
- **Keep [1.2 Current state](#12-current-state-2026-09-23) true.** It is the first thing a session reads.
- **Date every measurement and name the method and sample size.** Numbers from different harnesses or days aren't comparable. Put the comparison in one table from one run.
- **Record decisions *and rejections*** in [Part 9](#part-9--history-and-decisions) with the reason, so they aren't re-proposed without new evidence.
- **Check names, counts and constants with grep before writing them.**
- **The repo is public.** No API keys, voice recordings, transcripts, personal names or employer details, here or anywhere in the repo.
- **Machine-specific facts belong in "Current state"**, marked as such (which provider is active, which GGUFs are on disk). They differ between the desktop and the laptop.
