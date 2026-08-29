# WhisperInk — CLAUDE.md

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Global hotkeys capture audio and transcribe via multiple ASR backends, typing or pasting results into the foreground application.

## Repository

- GitHub: `praxeo/whisperinc` (main branch)
- Language: C# / WPF
- Runtime: .NET 8.0 (Windows)
- NuGet deps: `NAudio 2.2.1`, `Google.Apis.Auth 1.69.0` (the latter for Google Chirp 3 OAuth)

## On-disk layout

Three directories are involved:

1. **Source — `OneDrive\Desktop\whisperinc\`** — this repo (C#/WPF app). (On the desktop machine; path may differ per machine — resolve relative to the repo, never hardcode.)
2. **Source — `OneDrive\Desktop\CrispASR\`** — sibling clone of the native ASR binary (C++/CMake). Optional: since CrispASR v0.7 ships prebuilt Windows binaries, the normal update path is `scripts/update-crispasr.ps1` (see below) and the clone is only needed for source builds. It carries one uncommitted local patch (ggml-blas PkgConfig-optional fix).
3. **Runtime — `%APPDATA%\.WhisperInk\`** — hardcoded deploy target. Contains:
   - `config.json`, `debug.log`, `history.json` — app state
   - `cohere-gguf\` — `crispasr.exe` + all its DLLs + any `*.gguf` models for GGUF/Parakeet providers

The `%APPDATA%\.WhisperInk\cohere-gguf\` default folder is used by `CrispAsrServerTranscriber.cs`. Providers can override this per-entry via `ApiProvider.LocalModelFolder` (e.g. `"cohere-gguf-cuda"` for the CUDA preset) when the user keeps GGUFs in a different subdirectory.

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Orchestration: recording state machine (`_recState` tri-state via `Interlocked`), transcription dispatch (one factory call), config load/save, the shared `BuildAppMenu()` tree. ~1750 LOC. |
| `KeyboardHookService.cs` | Owns the `WH_KEYBOARD_LL` hook: modifier tracking, hotkey suppression, synthetic-event filter (0x5AFE marker), Ctrl+Space detection. Callbacks fire on the hook thread; MainWindow marshals via Dispatcher + `RunSafe`. |
| `MicCapture.cs` | Owns the microphone. Holds the device open between dictations ("warm mic") and streams every buffer into a `PreRollRing`, so `BeginCapture()` starts a recording that already contains the last ~400 ms. No device open on press, no teardown on release. Also hosts `PeakAmplitude()` for the silence gate. |
| `UiSoundPlayer.cs` | Low-latency UI chirps over one persistent `WaveOutEvent` + `BufferedWaveProvider`, with pre-synthesised tones and default-endpoint-change detection. Replaces per-chirp `System.Media.SoundPlayer`. |
| `TextInjector.cs` | Synthetic text delivery: clipboard paste-with-restore (batch), selection grab via Ctrl+C, `ReleaseAllModifierKeys()`. Owns pending clipboard-restore state. |
| `MenuModel.cs` | `MenuNode` record + `MenuSurface` (Both/TrayOnly/BarOnly) + WPF renderer. One canonical menu tree drives both the tray menu and the bar right-click menu — they cannot drift. |
| `TrayIcon.cs` | `TrayIconManager`: notification-area icon + WinForms renderer over the shared `MenuNode` tree (rebuilt on every `Opening`). |
| `AppConfig.cs` | `ApiProvider` model, `TranscriberKind` enum, default provider list. New providers added via `CreateDefaults()` or directly in `config.json`. |
| `ITranscriber.cs` | Common interface every batch backend implements (`TranscribeAsync(byte[], biasTerms, ct)`). |
| `TranscriberFactory.cs` | Lazy-caches one `ITranscriber` per provider id. `Drop(id)` to free a single model; `DropAll()` after settings edits. |
| `HttpTranscriber.cs` | OpenAI-compatible multipart POST. Covers Mistral batch, OpenAI Whisper, Cohere v2 cloud, ElevenLabs Scribe v2 (auth/keyterms/tag_audio_events/no_verbatim quirks gated on provider fields), Qwen3-ASR, and any user-added cloud provider. |
| `CrispAsrServerTranscriber.cs` | Generic adapter for `crispasr.exe --server`. Reads port / model glob / backend hint / GPU backend / model folder from the `ApiProvider`. One class for Parakeet, Cohere, Voxtral, Granite, Canary, …  |
| `GoogleChirp3Transcriber.cs` | Google Cloud STT v2 with OAuth + base64 JSON body and `adaptation.phraseSets` biasing. |
| `SonioxTranscriber.cs` | Soniox async REST job (`api.soniox.com/v1`): upload WAV → create transcription → poll status → fetch token array → concatenate → best-effort delete of job+file. Bias terms ride the `context.terms` field. Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
| `DeepgramTranscriber.cs` | Deepgram Listen API (`api.deepgram.com/v1/listen`): one synchronous POST with the WAV as the raw request body, `Authorization: Token`, all options (`model`/`smart_format`/`language`/`keyterm`) as query params, transcript at `results.channels[0].alternatives[0].transcript`. Bias terms ride Nova-3 keyterm prompting. Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for URLs, keys, auth header, model field, a read-only biasing-mechanism line, and the Parakeet hotword-boost / Scribe extras. ElevenLabs-only fields hide for other providers. |
| `ContextBiasWindow.xaml(.cs)` | Global context-bias term list. |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | Local transcript log + viewer. |

### Hotkeys

- **Ctrl+Space** — Hold to record, release to transcribe. Records WAV → HTTP POST to active provider → result pasted via clipboard (`Ctrl+V`).

### Dictation

Captures via `MicCapture`, POSTs the in-memory WAV, pastes with a leading space. Works with every provider. `~/Documents/MyRecordings/temp_audio.wav` is written **after** capture as a fire-and-forget replay/debug artifact only — it is no longer in the recording path (that folder is OneDrive-synced on the desktop, so a sync stall must never be able to stall a dictation).

### Capture pipeline & responsiveness (2026-08-29)

Three measured latencies used to make dictation feel slow and eat the first syllable. All were fixed by never opening a device on the hotkey path.

| Symptom | Root cause (measured on the desktop) | Fix |
|---|---|---|
| "Lag on the beep" | `SoundPlayer.PlaySync()` took **190–222 ms for a 30 ms tone** — winmm reopens the render endpoint per call | `UiSoundPlayer`: one persistent `WaveOutEvent`, enqueue **0.02–0.46 ms** |
| First part of the utterance clipped | press → first audio callback was **131–159 ms**; nothing said before that existed | Warm mic + **pre-roll ring**: the recording is seeded with audio from *before* the press |
| Stop felt sluggish | `StopRecording()` + `Dispose()` cost **112–134 ms** in the real pipeline log | Device is never stopped per dictation; only the writer detaches |

**Warm mic.** `MicCapture` keeps `WaveInEvent` open (`BufferMilliseconds = 50`) and every buffer flows into a `PreRollRing` whether or not a dictation is active. `BeginCapture()` attaches a `WaveFileWriter`, seeds it from the ring and flips a flag — all under one lock, so a buffer arriving mid-setup cannot fall through the gap. Measured: **2 ms**, with a full 400 ms of pre-press audio. `EndCapture(postRollMs)` waits (bounded, default 80 ms) for the in-flight buffer before flushing, so fixing the clipped start doesn't introduce a clipped end. Cost of staying warm is the Windows microphone-in-use indicator, hence the idle release (`WarmMicIdleSeconds`, default 180) and the 🎙 Microphone ▸ **Instant start** toggle.

`PreRollRing` is split out of `MicCapture` specifically so the wrap arithmetic is testable without a microphone — an off-by-one there wouldn't crash, it would quietly mis-order the first few hundred ms of every dictation. Verified byte-exact against a reference model over 400 randomised trials × 40 writes.

**Accidental presses and the lockout.** `_recState` stays at `Stopping` for the whole stop path, so anything slow there is a dead hotkey. Two guards now discard non-dictation before it can cost anything:

1. **Min-hold** (`MinHoldMs`, default 250) — press-to-release shorter than this is a brush against the key. Discarded with no API call, no error tone, no post-roll wait.
2. **Silence** (`SilenceThreshold`, default **0.003 RMS**) — covers "held it and then thought about what to say". Gated on **RMS, not peak**, and that distinction was measured, not assumed: two silent-room captures peaked at **0.0123 / 0.0124** (one fan or keyboard transient is enough) while their RMS was **0.00060 / 0.00124**. Speech RMS runs 0.01–0.1, so RMS separates by 20–100× where peak separated by 1.2× — a peak gate at any safe threshold simply never fires on the case it exists for. The 0.003 default sits 2.5–5× above the observed silence floor and 3–30× below speech. Both levels are logged on every capture (`[diag] captured …ms, RMS …, peak …`) so the threshold can be re-checked against real dictation, and the WAV is written to disk *before* the gate so a misjudged clip is recoverable.

Also gone: the `await Task.Delay(1500)` that ran **inside** the try block on the error path, holding `_recState` at `Stopping` for 1.5 s after every failed dictation — on top of the transcription round-trip it had just wasted (observed in `debug.log`: four consecutive ~2 s lockouts on 0.17–0.42 s accidental clips). Transient status text now goes through `FlashStatus()`, a Background-priority one-shot timer that never blocks the state machine. An empty-but-successful transcription is no longer treated as an error either — it gets the quiet `Dismissed` blip, not the 300 Hz error buzz.

**Tuning knobs** (all in `config.json`, all clamped on load): `WarmMicEnabled`, `WarmMicIdleSeconds` (0 = hold while running), `PreRollMs`, `PostRollMs`, `MinHoldMs`, `SilenceThreshold` (0 = disable the gate).

### Provider system

`ApiProvider` captures every knob; the cloud-vs-local split lives in `TranscriberKind`:

- **Shared HTTP/cloud knobs**: `BaseUrl`, `TranscriptionEndpoint` (override), `AuthHeaderName` (blank = Bearer, `xi-api-key` for ElevenLabs), `ModelFieldName` (`model` vs `model_id`), `TranscriptionModel`, `SupportsTranscription`, `TranscriptionTemperature` (nullable)
- **Bias**: one shared `AppConfig.ContextBiasTerms` list is routed to each provider's NATIVE field by `ApiProvider.BiasMechanism` (baked per provider in `CreateDefaults`; never user-set — `ResolvedBiasMechanism` falls back to the legacy `ContextBiasMode` for user-added providers). Mechanisms: `whisper_prompt` (labeled glossary in `prompt` — OpenAI, local shims), `mistral_context_bias` (comma string in `context_bias` — Mistral Voxtral batch, ≤100 terms), `elevenlabs_keyterms` (repeated `keyterms` from the shared list), `hotwords` (comma string — CrispASR local: real CTC/TDT/RNNT trie on Parakeet, opt-in via `HotwordsBoost` and **off by default** because boosting garbles neighboring words; prompt-injection on Voxtral-3B/Qwen3; accepted-but-no-op on Cohere/Granite/Voxtral-4B), `phrase_sets` / `context_terms` / `deepgram_keyterm` (Google / Soniox / Deepgram, handled natively in their transcribers — Deepgram routes the shared list to Nova-3 `keyterm` query params, or legacy `keywords` on older models), `none`. **Cohere Transcribe v2 has no biasing field** — its old `cohere_terms`→`context_bias_terms` was a phantom the server silently dropped, now removed.
- **ElevenLabs-only**: `TagAudioEvents`, `NoVerbatim`. Keyterms now come from the shared Context Bias list; `ScribeKeytermsRaw` remains an optional ElevenLabs-only supplement merged in (validated together: ≤1000 terms / <50 chars / ≤5 words, illegal chars dropped).
- **TranscriberKind**: `Http` | `LocalCrispAsrServer` | `GoogleChirp3` | `Soniox` | `Deepgram` — picks the `ITranscriber` implementation
- **LocalCrispAsrServer-only**: `LocalServerPort`, `LocalModelGlob` (e.g. `"parakeet-*.gguf"`), `LocalBackendHint` (e.g. `"cohere"` when auto-detect doesn't cover the GGUF), `LocalGpuBackend` (blank → fall back to global `CrispGpuBackend`), `LocalModelFolder` (blank → `cohere-gguf`), `LocalBeamSize` (nullable int → `beam_size` form field; **null = greedy** on the synced §166 build, since upstream `f1b5e546` made greedy the server default; editable in provider settings), `LocalPuncModel` (server-side punctuation model → crispasr `--punc-model`: `"fullstop"`/`"auto"`/`"firered"`/`"punctuate-all"`/`"pcs"`; blank → none; restores punctuation + sentence case for non-PnC backends like Parakeet RNNT/CTC — server-mode `--punc-model` is **upstream as of §166** (`36f35f2a`), no local patch needed), `LocalTruecaseModel` (server-side truecasing → crispasr `--truecase-model`: `"auto"`/`"crf"`/`"lstm"`/path; blank → none; applied after punctuation, a parallel spawn flag to `LocalPuncModel` — **wired but unset by default: the §166 truecase models over-capitalize (`auto`/`crf`) or no-op (`lstm`) on test audio, so leave off until upstream models improve**), `LocalExtraParams` (per-provider `Dictionary<string,string>` merged verbatim into the `/v1/audio/transcriptions` POST — reaches any §166 per-request field like `punctuation`/`vad`/`seed`/`suppress_nst` config-only, no recompile; reserved keys language/hotwords/hotwords_boost/beam_size/response_format/file are skipped)

Default providers (`AppConfig.CreateDefaults()`):

| Id | Name | TranscriberKind | Notes |
|----|------|-----------------|-------|
| `mistral` | Mistral | `Http` | Voxtral batch |
| `openai` | OpenAI | `Http` | Whisper-1, `whisper_prompt` bias |
| `elevenlabs` | ElevenLabs Scribe | `Http` | `xi-api-key` auth, `model_id` field, keyterms |
| `cohere-api` | Cohere Transcribe API | `Http` | Cohere v2, temp 0.1; no native biasing |
| `cohere-gguf-server` | Cohere Local (CrispASR, CPU) | `LocalCrispAsrServer` | Port 8766, `--backend cohere`, `cpu` |
| `qwen3-asr` | Qwen3-ASR Local | `Http` | Port 8102, user-managed external server |
| `parakeet-local` | Parakeet Local (CrispASR) | `LocalCrispAsrServer` | Port 8103, TDT 0.6b, glob `parakeet-tdt-*.gguf`, auto-detect backend |
| `parakeet-rnnt-local` | Parakeet RNNT 1.1b Local (CrispASR) | `LocalCrispAsrServer` | Port 8109, RNNT 1.1b q4_k, glob `parakeet-rnnt-1.1b-*.gguf`, auto-detect backend, server-side punctuation via `LocalPuncModel="fullstop"` (RNNT emits no punctuation natively) |
| `cohere-local-q6k` | Cohere Local Q6_K (CrispASR) | `LocalCrispAsrServer` | Port 8105, Q6_K GGUF, near-F16 accuracy |
| `voxtral-local` | Voxtral Local (CrispASR) | `LocalCrispAsrServer` | Port 8106, `--backend voxtral`, `hotwords` bias (3B GGUF) |
| `voxtral4b-local` | Voxtral 4B Realtime Local (CrispASR) | `LocalCrispAsrServer` | Port 8108, `--backend voxtral4b` — upstream treats the 4B realtime checkpoint as a distinct backend from the 3B |
| `granite-local` | Granite Speech 4.1 Local (CrispASR) | `LocalCrispAsrServer` | Port 8107, `--backend granite`; biasing is a no-op (backend has no hotword/prompt splice) |
| `google-chirp3` | Google Chirp 3 | `GoogleChirp3` | OAuth + JSON body, native `phraseSets` biasing |
| `soniox` | Soniox | `Soniox` | Async REST (`api.soniox.com/v1`), `stt-async-v5`, Bearer key, `context.terms` biasing |
| `deepgram` | Deepgram Nova-3 | `Deepgram` | Listen API (`api.deepgram.com/v1/listen`), `nova-3`, `Token` auth, `smart_format`, Nova-3 `keyterm` biasing |
| `deepgram-medical` | Deepgram Nova-3 Medical | `Deepgram` | Separate preset: `nova-3-medical`, same path/auth/biasing; English-only; calibration params off by default (see below), tunable via `DeepgramExtraParams` |

### Dispatch pipeline (`TranscriberFactory` + `ITranscriber`)

`MainWindow.xaml.cs:TranscribeAudioAsync` is a single factory lookup — no per-provider branching:

```csharp
var transcriber = _transcribers.GetOrCreate(provider);
if (!transcriber.IsReady(out var diag)) { Log(diag); return null; }
return await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms);
```

The factory caches one `ITranscriber` per provider id. Switching providers calls `Drop(oldId)` so we don't keep ~2 GB of resident model. Saving the settings dialog calls `DropAll()` because any field on any provider may have been edited.

### CrispAsrServerTranscriber (the generic one)

The path for every GGUF model. The constructor reads everything from the `ApiProvider`: port, model glob, backend hint, GPU backend (with fallback to the global `CrispGpuBackend`), model folder. First `TranscribeAsync` call lazy-spawns `crispasr.exe --server -m <model> --host 127.0.0.1 --port <port> -t <threads> -np [--backend X] [--punc-model FNAME] [--truecase-model FNAME]` (plus `-ng` when the effective backend is `cpu`, or `--gpu-backend X` when it's a specific GPU; `auto` passes nothing so ggml's `init_best` picks CUDA > Vulkan > CPU per what the binary was built with), waits up to **120s** for `/health` (v0.7+ auto-warms the model in server mode, so first health on a CUDA build includes VRAM upload + warmup), and posts subsequent audio to `/v1/audio/transcriptions` with `language`, `hotwords` (+ `hotwords_boost` when `HotwordsBoost` is set) whenever bias terms exist, optional `beam_size`, and any `LocalExtraParams` form fields (reserved keys skipped, added before the `file` part to keep all string fields ahead of it). The OpenAI `prompt` field is NOT sent — no CrispASR backend reads it (Voxtral/Qwen3 splice `hotwords` into their prompt themselves). Thread count is capped at `Min(8, ProcessorCount)` deliberately: ggml ASR scales with physical cores/memory bandwidth, not SMT, and `-t` barely matters on GPU backends. Server keeps model resident; process tree killed on `Dispose()`. When `LocalPuncModel`/`LocalTruecaseModel` are set, the spawn adds `--punc-model`/`--truecase-model` so the resident server restores punctuation (FireRedPunc) and casing per segment — server-mode `--punc-model` is **upstream as of §166** (`36f35f2a` + PCS/CTC auto-enable `8d803f04`); no local patch needed.

The legacy `CohereGgufTranscriber.cs`, `CohereGgufServerTranscriber.cs`, `CohereGgufCudaServerTranscriber.cs`, and `CohereGgufCudaQ8ServerTranscriber.cs` files have been deleted — the same providers now use this generic class via config-only entries.

### Soniox async REST (SonioxTranscriber)

Soniox has no synchronous "POST audio → text" endpoint, so each dictation is a four-step job on `api.soniox.com/v1` (Bearer auth): `POST /files` (multipart) → `POST /transcriptions` (JSON: `model`, `file_id`, `language_hints`, optional `context.terms`) → poll `GET /transcriptions/{id}` until `status` is `completed`/`error` → `GET /transcriptions/{id}/transcript`. The transcript is a `tokens[]` array whose text carries its own leading spacing, so concatenation rebuilds the sentence (a flat `text` field is a fallback). Polling is every 400 ms with a 120 s ceiling; each request is bounded by the shared 15 s `HttpClient` timeout. A `finally` block best-effort `DELETE`s both the transcription and the uploaded file (even on cancel/error) so nothing accumulates under the key. Default model `stt-async-v5` (current GA as of 2026-06-11; `stt-async-v4` is deprecated, removed 2026-06-30) is user-editable, so a Soniox model rename is a config edit, not a recompile. Context-bias terms map straight onto `context.terms` (real vocabulary steering, capped at 100), so `BiasMechanism` is informational only — same approach as Google Chirp 3.

### Deepgram Listen (DeepgramTranscriber)

Deepgram is a one-shot synchronous POST — no job/poll cycle like Soniox. Each dictation `POST`s the WAV as the **raw request body** (`Content-Type: audio/wav`, no multipart) to `api.deepgram.com/v1/listen`, with auth `Authorization: Token <ApiKey>` (not Bearer) and every option carried as a **URL query parameter**: `model` (default `nova-3`, user-editable), `smart_format=true` (baked on — restores punctuation/casing/number formatting for dictation), and `language` when set (omitted for `auto`). The transcript is read from `results.channels[0].alternatives[0].transcript`. One request, bounded by the shared 15 s `HttpClient` timeout (short clips return in ~1–3 s). Context-bias terms map onto **Nova-3 keyterm prompting** via repeated `keyterm` query params (real vocabulary steering, capped at 100); for non-Nova-3 models the transcriber falls back to the legacy `keywords` param, so biasing survives a user model swap. `BiasMechanism` (`deepgram_keyterm`) is informational only — same native-routing pattern as Google Chirp 3 / Soniox. Because it isn't OpenAI-compatible (raw body, query-param options, Token auth, nested response), it bypasses `HttpTranscriber` and has its own `TranscriberKind.Deepgram` factory branch.

**`ApiProvider.DeepgramExtraParams`** (`Dictionary<string,string>`) is the Deepgram analog of `LocalExtraParams`: any entry is appended verbatim (URL-encoded) to the `/v1/listen` query string, so calibration/formatting knobs (and any future Deepgram param) are config-only with no recompile. Reserved keys `model`/`smart_format`/`language`/`keyterm`/`keywords` are skipped so a stray entry can't fight the wired-in values. Parsed in `LoadConfig`, deep-copied in `CloneProvider`.

**Two Deepgram presets:** `deepgram` (general `nova-3`) and `deepgram-medical` (`nova-3-medical`, English-only, medical vocabulary). The medical preset ships with **empty `DeepgramExtraParams`** — a live A/B on real clinical clips rejected every calibration knob for unattended dictation that pastes straight into the EHR: `dictation=true` **stripped the terminal period and downcased `CT`→`Ct`** (hard reject — and it's *not* in `ReservedParams`, so it passes through if a user opts in); `measurements` risks ISMP unit abbreviations (units→U, IU); `numerals` over-converts idiomatic number-words ("cranial nerve two"→"2"); `filler_words` is unsupported on `nova-3-medical`; `paragraphs` is subsumed by the baked-in `smart_format`. Each is a documented per-clinician opt-in (add to that preset's `DeepgramExtraParams`), not a default. **Quirk:** the API reports `arch: nova-3` for `model=nova-3-medical` (internal name `medical-nova-3`) — expected, *not* a silent fallback; medical vocab is provably active (it recovers `ureterolithiasis` as one token where general Nova-3 mangles it).

### Text injection (`TextInjector.cs`)

- **Batch**: `Clipboard.SetText` + synthetic `Ctrl+V`. Leading space prepended to avoid word-fusion. Prior clipboard contents are cloned and restored ~250ms after the paste; chained dictations reuse the pending saved data so the user's original clipboard survives rapid-fire use.

### Keyboard hook internals (`KeyboardHookService.cs`)

- `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` installed on the UI thread; the hook delegate is held in a field (GC'ing it kills the hook silently).
- Modifier-release key-ups are tagged with sentinel `0x5AFE` (`TextInjector.SyntheticMarkerValue`) in extra-info so the hook ignores its own injections.
- `TextInjector.ReleaseAllModifierKeys()` runs after every recording to clear any physical modifier still held when the hook fired — prevents "stuck Ctrl" after long sessions.
- Recording lifecycle is one tri-state `_recState` (Idle/Recording/Stopping) transitioned via `Interlocked.CompareExchange` — double-start/double-stop are structurally impossible. Async command methods are `async Task`, dispatched through `RunSafe` so faults land in `debug.log`.

### Unified menu (`MenuModel.cs`)

`MainWindow.BuildAppMenu()` builds one `MenuNode` tree; the WPF renderer (bar right-click) and the WinForms renderer in `TrayIconManager` (tray right-click, rebuilt on every `Opening`) both render it, filtered by `MenuSurface` (`TrayOnly`: status header + Show Window; `BarOnly`: Hide to tray). Adding a menu item means adding one node — it appears on both surfaces.

### Cohere v2 multipart quirk

Cohere v2 rejects requests where `file` appears before string fields. WhisperInk always appends string fields (`model`, `language`, `temperature`, and any bias field such as `context_bias` / `keyterms`) **before** the `file` part.

### UI sounds

Chirps are procedurally generated sine waves — no asset files — synthesised **once** at startup and played through `UiSoundPlayer`'s persistent output device. Five tones: `Start` (1200 Hz), `Stop` (800 Hz), `Success` (1600 Hz), `Error` (300 Hz), and `Dismissed` (520 Hz, quieter — "I saw the press and threw it away", deliberately not the error buzz). Each has a 4 ms attack ramp; the old synth started the envelope at full amplitude, which put a click in front of every chirp.

Holding the render device open means WAVE_MAPPER binds to whatever was default at open time, so `UiSoundPlayer` re-checks the default endpoint before each tone (~3 ms, measured) and reopens when it changed — otherwise plugging in headphones would keep chirping at the speakers.

## Build & publish

```powershell
# Framework-dependent (smaller; requires .NET 8 Desktop Runtime on target)
dotnet publish -c Release -r win-x64 --self-contained false

# Self-contained (bundles runtime, ~80 MB)
dotnet publish -c Release -r win-x64 --self-contained true
```

Helper scripts: `publish.ps1` (self-contained), `publish-framework-dependent.ps1`.

### Updating CrispASR (preferred: prebuilt releases)

Since v0.7, `CrispStrobe/CrispASR` publishes prebuilt Windows binaries per release: `crispasr-windows-x86_64-{cpu,cuda,vulkan,cpu-legacy}.zip`. The normal update path is:

```powershell
# CUDA build (default — bundles cublas/cudart 12.x DLLs, ~880 MB deployed)
scripts\update-crispasr.ps1 -Tag v0.7.1

# Or the Vulkan / CPU variants
scripts\update-crispasr.ps1 -Tag v0.7.1 -Asset crispasr-windows-x86_64-vulkan.zip
```

The script downloads the asset via `gh`, stops any running crispasr server (WhisperInk respawns on next dictation), backs up the current `crispasr.exe` + `*.dll` to `cohere-gguf\.old-<stamp>\`, swaps in the new binaries (GGUFs untouched), and smoke-tests `--help` — empty output is the `STATUS_DLL_NOT_FOUND` signature and triggers an automatic restore from the backup. Keep the script **pure ASCII**: PowerShell 5.1 reads BOM-less files as ANSI, and multi-byte punctuation decodes into stray smart-quote bytes that break the parser.

A CUDA build with global backend `auto` lights up NVIDIA GPUs automatically (`init_best`: CUDA > Vulkan > CPU); presets pinned `LocalGpuBackend: "cpu"` still run CPU via `-ng`. v0.7 also brings server-mode hotwords/beam-search fields (wired into `CrispAsrServerTranscriber`), auto-warmup at server start, and a `/load` model-hot-swap endpoint (unused so far). Upstream has no CLAUDE.md — its agent file is a 3-line `AGENTS.md`; the real docs are `README.md`, `docs/` (server.md, cli.md), `PERFORMANCE.md`, `ARCHITECTURE.md`.

**⚠️ CrispASR sync — desktop now runs upstream `main` @ pin `9eecfd43` (§166 parity + §165 server perf), CUDA build (sm_86), deployed 2026-06-13.** Supersedes the `4b27392f`+#161-punc build. Three things changed: **(1)** the local server-punctuation patch is now **upstream** (`36f35f2a` + the §166 parity series — PCS/CTC punc auto-enable `8d803f04`, per-request knobs `436a707a`, `/v1/translate`, WebSocket streaming), so it is no longer a local patch — **the *only* remaining local change is the ggml-blas PkgConfig-optional patch** (re-apply after any pull; inert on CUDA-only builds where BLAS isn't found, but kept). **(2)** The #161 perf fix (`4b27392f`) is upstream and confirmed on CUDA (`6ee3f6a0`); upstream `f1b5e546` makes **greedy the server default**, so `LocalBeamSize: null` now means *greedy* (not beam-5). **(3)** Pinned at `9eecfd43` deliberately — everything after it on `main` (through `657e851e`) is voxcpm2/nemotron/silero/LID churn on TTS / non-ASR backends WhisperInk never calls. **A/B before deploy** (2026-06-13, `jfk.wav`, beam 1, warm): new `9eecfd43` ≈ old `4b27392f` within noise — cohere q6_k greedy median ~317–352 ms (order-dependent; new marginally *faster* when run first), parakeet-rnnt+fullstop ~740–780 ms; transcripts identical, punctuation restored. **No regression.** Built into `CrispASR\build-sync\bin\Release\`; the prior `4b27392f`+punc binary is backed up in `cohere-gguf\.old-2026-06-13-1602\` (rollback), and `build-161\` still holds its source. *History:* v0.7.1 regressed cohere-CUDA ~10× / CPU ~2× vs the 2026-05-03 build; root cause was a `beam_size=5` default whose cohere beam search snapshotted the KV cache through host memory (#161 — fixed by `4b27392f`'s on-device snapshots; warm q6_k CUDA greedy **0.31s**). May-3 set archived in `.old-2026-06-12-1354\`, v0.7.1 in `.v0.7.1-cuda-regressed\`. The laptop still runs a pre-v0.7 binary. **Still A/B any newer upstream build/release against the deployed pin before switching** — [CrispASR#161](https://github.com/CrispStrobe/CrispASR/issues/161) is still open as an issue and `main` keeps moving. *(Note: `scripts/build-crispasr.ps1` does `git pull --ff-only` on the checked-out branch; the clone's `main` now sits at the pin, behind `origin/main`, so running the script as-is would fast-forward past `9eecfd43` into the voxcpm2 churn — for a pinned rebuild, build manually into a fresh dir or check out the pin first.)*

**Truecase (§166) — wired but OFF by default.** `ApiProvider.LocalTruecaseModel` → crispasr `--truecase-model` (a resident startup post-processor parallel to `LocalPuncModel`, applied after punctuation). The plumbing is validated end-to-end on the deployed binary, but the §166 truecase models are low-quality on test audio: `auto`/`crf` **over-capitalize** content words (`fullstop`+`auto` → *"my **Fellow** americans ask **Not** what **Your Country Can**…"*; `crf` gets proper nouns but still over-caps) and `lstm` is a **no-op** (identical to punc-only). So no preset sets it — revisit when the upstream truecase checkpoints improve. The sibling companion `ApiProvider.LocalExtraParams` (per-provider `Dictionary<string,string>`) reaches the other §166 per-request fields (`punctuation`, `vad`, `seed`, `suppress_nst`, …) config-only, no recompile. Both fields parse + backfill in `LoadConfig` and copy in the `ProviderSettingsWindow` clone like the other `Local*` fields. (Bonus fix landed with this sync: `LocalPuncModel` now actually *parses* from `config.json` — previously it only got its value via default-backfill.) Build target remains **`crispasr-cli`** (not `whisper-cli`). **Deferred next phase:** WebSocket realtime dictation via the new `--ws-port` endpoint (whisper-only today → needs a whisper-GGUF provider + a streaming float32 mic path + partials UI; re-introduces the realtime mode removed 2026-06-13). **Dropped for now:** translate (revisit when mature — needs either granite AST or a ~502 MB m2m100 model + second server).

### CrispASR native build (alternative: from source)

`scripts/build-crispasr.ps1` clones `CrispStrobe/CrispASR` as a sibling of this repo (resolving via `$PSCommandPath` so no hardcoded paths), configures with the VS2022 generator, builds the `whisper-cli` target (which produces `crispasr.exe` via `OUTPUT_NAME`), and deploys the binary + all `*.dll` files to `%APPDATA%\.WhisperInk\cohere-gguf\`.

Key flags baked into the script:
- `-G "Visual Studio 17 2022" -A x64` — newer generators fail on Build Tools 2022 only installs
- `-DGGML_CUDA=OFF` — flip to `ON` for GPU builds (requires CUDA Toolkit 12.x)
- `-DWHISPER_BUILD_TESTS=OFF` — skips the Catch2 FetchContent that fails on offline/firewalled networks
- `--target whisper-cli` — the unified multi-backend CLI; `whisper-server` is a separate legacy target and **not** what WhisperInk uses (server mode is built into `crispasr.exe` via the `--server` subcommand)

Deploy copies **every** `*.dll` from `build\bin\Release\`, not just `ggml*`. The binary statically links the per-backend `parakeet.dll`, `canary.dll`, `cohere.dll`, `crispasr.dll`, etc. at load time; missing any of them causes `STATUS_DLL_NOT_FOUND` (exit code `-1073741515` / `0xC0000135`), which presents as a silent exit with no output. If a user reports "crispasr runs but returns empty transcripts" or "`--help` prints nothing," the first thing to check is whether every build DLL is present in `cohere-gguf\`.

## Adding a new GGUF model

Config-only — no recompile required.

1. Download the GGUF into `%APPDATA%\.WhisperInk\cohere-gguf\` (or a sibling folder if you set `LocalModelFolder`).
2. Add a provider entry to `%APPDATA%\.WhisperInk\config.json`:
   ```json
   {
     "Id": "canary-local",
     "Name": "Canary Local (CrispASR)",
     "BaseUrl": "http://localhost:8110",
     "TranscriberKind": "LocalCrispAsrServer",
     "LocalServerPort": 8110,
     "LocalModelGlob": "canary-*.gguf",
     "ContextBiasMode": "none",
     "Language": "en"
   }
   ```
   (Ports 8103 and 8105–8109 are taken by the shipped presets — 8109 is `parakeet-rnnt-local`, which is why this canary example moved to 8110; the two Parakeet presets share `cohere-gguf\` and rely on disjoint globs (`parakeet-tdt-*` vs `parakeet-rnnt-1.1b-*`) so neither hijacks the other. 8104/8767/8768 belonged to the retired cuda/cuda-q8/Q4 cohere presets — removed from `CreateDefaults()` 2026-06-12 because the additive default-merge in `LoadConfig` resurrected them on every launch after the user deleted them. Their Ids still resolve in `KindForId`/`HealthProbe`/`ProviderDiagnostics` for configs that carry them.) Set `LocalBackendHint` if the GGUF doesn't auto-detect (Cohere needs `"cohere"`, Voxtral 3B `"voxtral"`, Voxtral 4B Realtime `"voxtral4b"`, Granite `"granite"`).
3. Restart WhisperInk. The provider appears in the tray menu under 🔌 Provider, and `CrispAsrServerTranscriber` lazy-spawns the server on first dictation.

For a permanent default (shipped to every install), add the same entry to `AppConfig.CreateDefaults()` so new users get it on first run.

No C++ work needed — CrispASR auto-detects most backends from GGUF metadata.

## Common gotchas

- **Hook needs the window alive.** Closing the main window uninstalls the hook. Minimize, don't close.
- **`.NET 8 Desktop Runtime`** is required, not just the base runtime. The WPF assemblies live in the Desktop variant.
- **Mic enumeration is at launch.** Plug in before starting WhisperInk, or restart after plugging in.
- **Stuck modifier keys** should auto-clear via `ReleaseAllModifierKeys()`. If one persists, tapping the key once releases it — capture `debug.log` around the stuck event.
- **CrispASR silent failure = missing DLL.** Exit code `-1073741515` means `STATUS_DLL_NOT_FOUND`. Re-run `scripts\update-crispasr.ps1` (it deploys every `*.dll` from the release zip and auto-restores on a failed smoke test), or for source builds re-copy all `*.dll` from `build\bin\Release\`.
- **Slow CPU inference on laptops = Windows power plan.** The default **Balanced** plan throttles CPU to base clock (e.g., 2.0 GHz on a Ryzen 5825U) even while plugged in, which roughly halves ASR throughput. Create and activate **Ultimate Performance**:
  ```powershell
  powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61
  powercfg /setactive 5898ace7-acb8-479d-b9c1-54af0f151d1b
  ```
  Symptom: RTFx that should be 2–3× is 1–1.5×; CPU stuck at `MaxClockSpeed` in `Get-CimInstance Win32_Processor`.
- **`update_automation` MCP tool** (home-automation side, unrelated but documented for completeness) is broken on the user's HA instance — YAML edits must be manual.

## Repo hygiene

`.gitignore` excludes `publish/`, `bin/`, `obj/`, `build.log`, plus the local AI-tooling state dirs `.claude/` and `.roo/` (the latter holds an MCP config with credentials — never commit it; this repo is public). The CrispASR source tree lives in a sibling directory, not a submodule — intentionally, so CrispASR updates are a plain `git pull` in that folder without dragging submodule plumbing into this repo.

## Future work

- Batch pipeline state-machine extraction out of MainWindow (the riskiest remaining chunk; deferred deliberately).
- `-dev N` GPU-index pinning for multi-GPU machines (crispasr supports it; no provider knob yet).
- `TextInjector.GetSelectedText()` still uses a fixed 100ms sleep before reading the clipboard — replace with a clipboard-listener.
- The 15s `_httpClient` timeout is hardcoded.
- CrispASR `/load` hot-swap could collapse the one-port-per-model preset scheme into a single resident server.
