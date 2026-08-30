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
| `SmallestTranscriber.cs` | Smallest.ai Waves batch (`api.smallest.ai/waves/v1/stt/`): one synchronous POST with the WAV as the raw request body, `Authorization: Bearer`, `Content-Type: application/octet-stream`, all options as query params, transcript at top-level `transcription`. The model **is** a `model` query param (unlike Modulate), so both presets share one URL. **No biasing field exists on this API at all.** Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
| `Reson8Transcriber.cs` | Reson8 prerecorded (`api.reson8.dev/v1/speech-to-text/prerecorded`): one synchronous POST with the WAV as the raw request body, `Authorization: ApiKey` (a **third** auth scheme, alongside Deepgram's `Token` and Bearer), all options as query params, transcript at top-level `text`. **No `model` param and one endpoint** → one preset; the customization axis is `custom_model_id`. Real biasing via comma-separated `phrases` (≤250). Errors are RFC 7807 `problem+json` with a lowercase `code`. Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
| `ModulateTranscriber.cs` | Modulate Velma 2 batch (`platform.modulate.ai/api/velma-2-stt-batch*`): one synchronous multipart POST, audio part named `upload_file`, bare `X-API-Key` header, transcript at top-level `text`. The **model is the endpoint path**, not a form field, so one class serves all three batch models and the variant decides which fields are legal. Bias terms ride `custom_terms` inside the JSON `config` field. Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
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
- **Bias**: one shared `AppConfig.ContextBiasTerms` list is routed to each provider's NATIVE field by `ApiProvider.BiasMechanism` (baked per provider in `CreateDefaults`; never user-set — `ResolvedBiasMechanism` falls back to the legacy `ContextBiasMode` for user-added providers). Mechanisms: `whisper_prompt` (labeled glossary in `prompt` — OpenAI, local shims), `mistral_context_bias` (comma string in `context_bias` — Mistral Voxtral batch, ≤100 terms), `elevenlabs_keyterms` (repeated `keyterms` from the shared list), `hotwords` (comma string — CrispASR local; **two very different mechanisms share this one name**: (a) a real CTC/TDT/RNNT **trie** on Parakeet, opt-in via `HotwordsBoost` and **off by default** because boosting garbles neighboring words, and (b) a **decoder prompt-splice** on the speech-LLM backends — Voxtral-3B and Qwen3-ASR — where the terms are appended to the ChatML prompt as "…may appear in the audio". Path (b) on `qwen3-asr-1.7b-local` is the **only local biasing in WhisperInk that has ever been measured to reliably flip a hard term** (see below); accepted-but-no-op on Cohere/Granite/Voxtral-4B), `phrase_sets` / `context_terms` / `deepgram_keyterm` / `modulate_custom_terms` / `reson8_phrases` (Google / Soniox / Deepgram / Modulate / Reson8, handled natively in their transcribers — Deepgram routes the shared list to Nova-3 `keyterm` query params, or legacy `keywords` on older models; Modulate packs it into `custom_terms` inside the JSON `config` form field, Velma 2 Multilingual batch only; Reson8 joins it into a comma-separated `phrases` query param, ≤250 terms — and is the one provider where an over-long list **degrades** accuracy per upstream rather than being merely ignored), `none`. **Cohere Transcribe v2 has no biasing field** — its old `cohere_terms`→`context_bias_terms` was a phantom the server silently dropped, now removed.
- **ElevenLabs-only**: `TagAudioEvents`, `NoVerbatim`. Keyterms now come from the shared Context Bias list; `ScribeKeytermsRaw` remains an optional ElevenLabs-only supplement merged in (validated together: ≤1000 terms / <50 chars / ≤5 words, illegal chars dropped).
- **TranscriberKind**: `Http` | `LocalCrispAsrServer` | `GoogleChirp3` | `Soniox` | `Deepgram` | `Modulate` | `Smallest` | `Reson8` — picks the `ITranscriber` implementation
- **LocalCrispAsrServer-only**: `LocalServerPort`, `LocalModelGlob` (e.g. `"parakeet-*.gguf"`), `LocalBackendHint` (e.g. `"cohere"` when auto-detect doesn't cover the GGUF), `LocalGpuBackend` (blank → fall back to global `CrispGpuBackend`), `LocalModelFolder` (blank → `cohere-gguf`), `LocalBeamSize` (nullable int → `beam_size` form field; **null = greedy** on the synced §166 build, since upstream `f1b5e546` made greedy the server default; editable in provider settings), `LocalPuncModel` (server-side punctuation model → crispasr `--punc-model`: `"fullstop"`/`"auto"`/`"firered"`/`"punctuate-all"`/`"pcs"`; blank → none; restores punctuation + sentence case for non-PnC backends like Parakeet RNNT/CTC — server-mode `--punc-model` is **upstream as of §166** (`36f35f2a`), no local patch needed), `LocalTruecaseModel` (server-side truecasing → crispasr `--truecase-model`: `"auto"`/`"crf"`/`"lstm"`/path; blank → none; applied after punctuation, a parallel spawn flag to `LocalPuncModel` — **wired but unset by default: the §166 truecase models over-capitalize (`auto`/`crf`) or no-op (`lstm`) on test audio, so leave off until upstream models improve**), `LocalExtraParams` (per-provider `Dictionary<string,string>` merged verbatim into the `/v1/audio/transcriptions` POST — reaches any §166 per-request field like `punctuation`/`vad`/`seed`/`suppress_nst` config-only, no recompile; reserved keys language/hotwords/hotwords_boost/beam_size/response_format/file are skipped)

Default providers (`AppConfig.CreateDefaults()`):

| Id | Name | TranscriberKind | Notes |
|----|------|-----------------|-------|
| `mistral` | Mistral | `Http` | Voxtral batch |
| `openai` | OpenAI | `Http` | Whisper-1, `whisper_prompt` bias |
| `elevenlabs` | ElevenLabs Scribe | `Http` | `xi-api-key` auth, `model_id` field, keyterms |
| `cohere-api` | Cohere Transcribe API | `Http` | Cohere v2, temp 0.1; no native biasing |
| `cohere-gguf-server` | Cohere Local (CrispASR, CPU) | `LocalCrispAsrServer` | Port 8766, `--backend cohere`, `cpu` |
| `qwen3-asr-1.7b-local` | Qwen3-ASR 1.7B Local (CrispASR) | `LocalCrispAsrServer` | Port 8112, glob `qwen3-asr-1.7b-*.gguf`, `--backend qwen3-1.7b`. **The only local preset with biasing that actually works** — speech-LLM prompt-splice, see the `hotwords` note above. Native punctuation + casing, so no `LocalPuncModel`. Replaced the old `qwen3-asr` Http preset (port 8102, user-managed server) on 2026-08-29 |
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
| `modulate` | Modulate Velma 2 (Multilingual) | `Modulate` | `/api/velma-2-stt-batch`, `X-API-Key`, real `custom_terms` biasing (definitions + pronunciations available) |
| `modulate-english-fast` | Modulate Velma 2 English Fast | `Modulate` | `/api/velma-2-stt-batch-english-vfast`, lowest latency; English-only, no biasing, takes no `language` param |
| `modulate-multilingual-fast` | Modulate Velma 2 Multilingual Fast | `Modulate` | `/api/velma-2-stt-batch-multilingual-vfast`, any language, no metadata, no biasing |
| `smallest-pulse-pro` | Smallest.ai Pulse Pro (English) | `Smallest` | `waves/v1/stt/?model=pulse-pro`, Bearer, English-only. The accurate + steadier of the two. **No biasing of any kind** |
| `smallest-pulse` | Smallest.ai Pulse (Multilingual) | `Smallest` | Same URL, `?model=pulse`. 46 language codes; wider latency spread and drops terminal punctuation. **No biasing** |
| `reson8` | Reson8 | `Reson8` | `/v1/speech-to-text/prerecorded`, `ApiKey` auth. One preset (no `model` param). Real `phrases` biasing (≤250) + `custom_model_id` for 50k-phrase persistent vocabulary. **Ten languages only** — `Language="en"` pinned. **Not yet live-tested; no key configured** |

**Which one is actually active is per-machine** — `config.json` → `ActiveProviderId`, never the `"mistral"` literal in `AppConfig`. On the desktop that field read **`cohere-local-q6k`** when it was last inspected (2026-08-29, during the v0.8.30 sync); an earlier note in this file claimed `elevenlabs` (Scribe v2) — read the file, don't trust either sentence. That matters when reasoning about vocabulary: Scribe v2 is one of the cloud providers with *real* keyterm biasing (with `google-chirp3`, `soniox`, `deepgram`, and Modulate Multilingual), so the shared Context Bias list is live on that path.

**Local biasing is no longer a dead end (2026-08-29).** The long-standing verdict — Parakeet's trie never flipped a hard term, Cohere/Granite/Voxtral-4B ignore the field, and `deepgram-medical` was the only thing that could resolve `ureterolithiasis` — was true of every local preset *that existed at the time*. It is not true of `qwen3-asr-1.7b-local`. Measured on the six clips in `_scratch\biasing\clips\`, server mode, greedy:

| clip | no bias terms | with bias terms |
|---|---|---|
| `hematochezia_1` | "hemat**uria**" ❌ | "hematochezia" ✅ |
| `hematochezia_2` | "hemat**emesis**" ❌ | "hematochezia" ✅ |
| `ureterolithiasis` | "**bursitis with edema**" ❌ | "ureterolithiasis" ✅ |
| `biliary_colic` / `ureteral_colic` / `neutral` (controls) | correct | **unchanged** |

Three for three on the hard terms, **3/3 reproducible** on re-runs, and **no collateral damage to the controls** — which is exactly what Parakeet's `HotwordsBoost` could never do without garbling neighbours. The mechanism is different in kind: Parakeet re-weights lattice arcs, Qwen3-ASR is a speech-LLM and the terms are spliced into its decoder prompt, so an in-vocabulary-but-unlikely word simply becomes likely. **Caveat — the effect dilutes with list length**, it does not garble: a 40-term list (targets buried mid-list) still fixed 5 of 6, softening only `ureterolithiasis` into "ureteral lithiasis". So keep the Context Bias list tight when a specific term matters. `deepgram-medical` is still the stronger *unbiased* clinical model; the difference is that a local preset can now be steered at all.

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

### Modulate Velma 2 batch (ModulateTranscriber)

Modulate is a one-shot synchronous multipart POST, like Deepgram — no job/poll cycle. Each dictation POSTs the WAV as an `upload_file` part (**not** `file`) to one of three batch endpoints on `platform.modulate.ai`, authenticated with a bare `Authorization`-less **`X-API-Key: <key>`** header. The transcript is the top-level `text`, identical across all three endpoints, so one parse covers every variant.

The defining quirk: **the model is the endpoint path, not a form field.** There is no `model` parameter — picking English Fast over Multilingual means POSTing to a different URL. That's why all three presets leave `TranscriptionModel` blank and differ only by `TranscriptionEndpoint`, and why `ModulateTranscriber` derives a *variant* from that URL and sends only the fields that endpoint documents:

| Variant | Endpoint | Fields sent |
|---|---|---|
| Multilingual | `/api/velma-2-stt-batch` | `speaker_diarization=false`, `language`, `config` (when bias terms exist), `upload_file` |
| English Fast | `/api/velma-2-stt-batch-english-vfast` | `upload_file` only |
| Multilingual Fast | `/api/velma-2-stt-batch-multilingual-vfast` | `language`, `upload_file` |

The endpoint paths are `public const` on `ModulateTranscriber` and `AppConfig.CreateDefaults()` builds the preset URLs from them, so a preset URL and the variant detection cannot drift apart — a drift there wouldn't fail loudly, it would quietly send the wrong field set. An unrecognised URL falls back to Multilingual (its parameter set is a superset of the other two).

**Every enrichment signal is deliberately off.** `speaker_diarization`, `emotion_signal`, `accent_signal`, `deepfake_signal` and `pii_phi_tagging` exist for call analytics; WhisperInk reads only `text`, so they are pure added latency here. `pii_phi_tagging` is actively harmful — it wraps sensitive spans in entity **tags inside the transcript text**, which would paste as markup into the target app. Note `speaker_diarization` **defaults to `true`** on the Multilingual endpoint, so the transcriber sends `false` explicitly rather than trusting the default — the same reason `HttpTranscriber` must send `tag_audio_events` explicitly for ElevenLabs. There is deliberately **no `ModulateExtraParams`** passthrough (unlike `DeepgramExtraParams`): every knob Modulate exposes here degrades dictation, so there's nothing worth opting into.

**Biasing** is the richest of any WhisperInk provider: Multilingual's `custom_terms` accepts plain strings *or* objects carrying a `definition` and `pronunciations` (X-SAMPA or `GOO-guhl`-style respelling). The shared `ContextBiasTerms` is a `List<string>`, so plain strings are what we emit; the object form is available if a per-term editor is ever built. `custom_terms` travels **only** inside the JSON `config` form field — there is no top-level form field for it. Because a field set in `config` overrides its top-level twin, nothing else goes in that object. Terms are clamped to Modulate's 1000-entry limit and its 8000-character serialized budget (we stop at 7800 to leave room for JSON escaping), and any clamp is logged.

The multipart part filename must end in `.wav` — Modulate validates the audio format **by file extension**, so the part is added as `audio.wav`. String fields go before the file part, matching the house convention. Auth failures come back as `401` on Multilingual Fast and `403` on the other two, so `DescribeError` surfaces the `detail` string and names the cause ("check the Modulate API key") rather than the bare code.

### Smallest.ai Waves batch (SmallestTranscriber)

A one-shot synchronous POST, closest in shape to Deepgram: the WAV is the **raw request body** (`Content-Type: application/octet-stream`, no multipart) to `https://api.smallest.ai/waves/v1/stt/` — the **trailing slash is load-bearing** — with `Authorization: Bearer <key>` and every option as a **query parameter**. The transcript is the top-level `transcription`. Unlike Modulate, the model is a real `model` query param, so both presets share one URL and differ only by `TranscriptionModel`. Measured on `jfk.wav`: server-side ~180–200 ms at rtfx 55–62, ~0.4–1.4 s wall-clock including upload.

**`language` is the parameter that can kill a dictation, and it took live probing to get right.** Validation is strict-enum, so WhisperInk's house `auto` sentinel is not passable the way `DeepgramTranscriber` passes it — and passing the enum is necessary but *not* sufficient, because a code can be enum-legal and still refused for the account's region with `error_code=LANGUAGE_NOT_ENABLED_IN_REGION`. Verified against the live API on this key:

| Case | Result |
|---|---|
| `pulse-pro`, any code but `en` | **400** — its enum is exactly `["en"]`, so the transcriber coerces to `en` and logs it |
| `pulse`, `language` omitted | **400** — the server falls back to its own `multi` default, which is region-gated |
| `pulse`, `multi` / `multi-indic` | **400** `LANGUAGE_NOT_ENABLED_IN_REGION` (enum-legal, not entitled) |
| `pulse`, `multi-eu` / `multi-asian` | **200** |
| `pulse-pro`, `language` omitted | 200 (defaults to `en`) |

So the transcriber **always sends an explicit language**, `auto` maps to `multi-eu` (the broadest aggregator confirmed enabled, and it covers English) rather than to the spec's `multi`, and `DescribeError` surfaces `error_code` — that code is the only thing distinguishing a region gate from a typo. The real enum is **46 codes**, not the 26 the published reference lists; the full set was read off the API's own validation error, and it includes several (`multi`, `yue`, `north_indic`, `multi-south-indic`, …) the docs omit entirely.

**There is no biasing field on this API — a true `none`.** No keyterm, hotword, or custom-vocabulary parameter exists on the pre-recorded endpoint, so the shared `ContextBiasTerms` list cannot be routed anywhere. That is worth stating plainly because it is a *different* failure from Cohere v2's old phantom field: a mis-recognized term here simply cannot be corrected. The transcriber logs the ignored count on every call rather than dropping the list silently.

**Measured on the six clinical clips** (`_scratch\biasing\clips\`), and this is the reason to reach for it or not:

| clip | `pulse-pro` | `pulse` |
|---|---|---|
| `hematochezia_1` | "hemat**emesis**" ❌ | "hemato**chesia**" ❌ |
| `hematochezia_2` | "hemat**emesis**" ❌ | "hemato**chesia**" ❌ |
| `ureterolithiasis` | "**uretero-ovaryosis**" ❌ | "**ureterithiasis**" ❌ |
| `biliary_colic` / `ureteral_colic` / `neutral` | correct | correct |

Both miss all three hard terms, and with no biasing surface there is no lever to fix them. Note the *kind* of error differs: `pulse` produces near-miss spellings of the right word, while `pulse-pro` substitutes **hematemesis for hematochezia** — a clinically opposite finding (upper- vs lower-GI bleeding), which is the more dangerous failure in a transcript that pastes straight into a chart. `pulse-pro` is nonetheless the better dictation preset on everything else: 1.37–1.69 s across the six clips against `pulse`'s 0.85–2.40 s, and terminal punctuation on 6/6 where `pulse` dropped it twice. **`deepgram-medical` and `qwen3-asr-1.7b-local` remain the only providers that resolve these terms.**

**Every enrichment knob is deliberately off** and there is no `SmallestExtraParams` passthrough (unlike `DeepgramExtraParams`). `word_timestamps`, `diarize`, `emotion_detection` and `gender_detection` all default false and are pure added latency here — we read only `transcription`, and `word_timestamps` alone costs roughly a third of Pulse Pro's throughput. `redact_pii` / `redact_pci` are actively destructive for dictation: they **replace words in the transcript** with `[FIRSTNAME_1]` / `[PHONENUMBER_1]` tokens, which would paste as literal markup — the same failure mode as Modulate's `pii_phi_tagging`. `webhook_url` is never sent either: it switches the 200 body to `{"status":"processing","request_id":…}` with no transcript, which for a dictation tool is just a lost utterance (`ParseTranscript` detects that shape anyway and names the cause). Unknown query params are *ignored* rather than rejected, so a passthrough could be added safely — there is simply nothing here worth opting into.

**Retention caveat:** the API retains request content by default. The `x-expire-content: true` header opts into 7-day deletion but is documented **Enterprise-plans-only**, so it is not sent. Worth weighing before pointing clinical dictation at this provider.

### Reson8 prerecorded (Reson8Transcriber)

A one-shot synchronous POST, same family as Deepgram/Smallest: the WAV is the **raw request body** (`Content-Type: application/octet-stream`) to `https://api.reson8.dev/v1/speech-to-text/prerecorded`, every option is a **query parameter**, and the transcript is the top-level `text`. Auth is `Authorization: ApiKey <key>` — a **third** scheme in the codebase, alongside Deepgram's `Token` and everyone else's `Bearer`. Errors are RFC 7807 `application/problem+json` carrying a lowercase `code`, so `DescribeError` reads `code` + `detail`/`title` rather than any of the shapes the other transcribers parse.

**One endpoint, no `model` param — hence one preset.** Worth stating because the two preceding Case-C additions were the opposite: Modulate selects its model by *endpoint path* (three presets) and Smallest by a real `model` query param (two presets). Reson8's customization axis is neither — it is `custom_model_id`, a persistent vocabulary built out-of-band in the console. `TranscriptionModel` is unused on this path and left blank.

**Biasing is real, and it is the reason to care.** `phrases` takes a comma-separated list of up to 250 terms, so the shared `ContextBiasTerms` routes straight onto it. Two things make it different from the other bias-capable providers:

- **An over-long list actively hurts.** Upstream is explicit that "a large set of irrelevant ones can degrade transcription," and that everyday words "only dilute the model." Elsewhere a bloated list is wasted (Smallest ignores it entirely; Cohere accepted-and-dropped its phantom field); here it is a *cost*. Same directional finding as the qwen3-asr dilution note — keep the list tight.
- **Comma is the delimiter**, so a term containing one would silently split into two bogus phrases. Terms are sanitized (commas → spaces, logged) rather than dropped: `"Smith, John"` still biases usefully as `"Smith John"`.

Clamped to 250 terms **and** a 4000-char budget — the count limit alone doesn't bound the URL, since `phrases` rides in the query string and percent-encoding inflates it; a 414 would lose the dictation as surely as a 400. For vocabulary past 250 terms, the upstream answer is a custom model (up to 50,000 phrases) via `custom_model_id`.

**`language` is the inverse of the Smallest.ai trap, and it can 400 away every press.** There, an explicit language must *always* be sent because the server's own default is region-gated. Here, auto-detection is requested by **omitting** the parameter — there is no `auto` value — so WhisperInk's house sentinel must become an *absent* param rather than a forwarded one. Reson8 supports exactly ten languages (`de/en/es/fr/fy/it/nl/pl/pt/sv`), and **WhisperInk's own language dropdown offers six that are not among them** (`ja/ko/zh/ru/ar/hi`). Picking one from the combo box would otherwise 400 with `invalid_query_parameter` on every dictation, so `ResolveLanguage` drops unsupported codes (per-code, since comma-separated lists are legal) and falls back to auto-detect with a loud log line. The preset pins `Language = "en"` deliberately: upstream notes detection is "less reliable for short utterances," and short utterances are all a push-to-talk tool ever sends.

**`ApiProvider.Reson8ExtraParams`** is the Deepgram-style passthrough, and unlike Modulate/Smallest this provider *earns* one — the rule there was "every knob degrades dictation," which isn't true here. Three are worth opting into: `custom_model_id` (the strongest biasing lever this API has), `filler_mode` (`clean` drops "um"/"uh"; default `natural` lets the model decide; `verbatim` keeps them — left at the default because it silently rewrites transcripts), and `patterns` (regex recovery of short alphanumeric tokens). Reserved keys are `language`/`phrases` plus `encoding`/`sample_rate`/`channels` — the latter three for a different reason than the rest: the transcriber posts a complete WAV, so declaring `pcm_s16le` would make the server misparse the 44-byte RIFF header as audio and prepend a burst of noise to every transcript.

Response-shaping knobs (`include_timestamps`/`include_words`/`include_language`/`include_confidence`/`diarize`/`max_speakers`) are all left off — we read only `text`, so each is pure added latency. None of them *corrupt* the transcript the way Modulate's `pii_phi_tagging` or Smallest's `redact_pii` would: `text` stays the full clean transcript even under `diarize=true`, where `segments` are added *alongside* it. `ParseTranscript` keeps a segments fallback anyway as cheap insurance.

**Status: wired and wire-verified, but NOT live-tested.** No API key is configured and no clip has gone through the real service, so there are no accuracy or latency numbers — in particular nothing yet on the six clinical clips, where `deepgram-medical` and `qwen3-asr-1.7b-local` are still the only providers that resolve the hard terms. What *has* been verified is the request shape: a probe (`_scratch/reson8/`) links the shipping `Reson8Transcriber.cs` against a local `HttpListener` and asserts 52 properties of the actual wire format — auth header, raw-body content type, `encoding=auto`, every language-resolution branch, phrase clamping/sanitization, reserved-key enforcement, all five error statuses, and the preset's own composition. That covers "we send what the docs describe"; it does not cover "the service transcribes well."

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
# CUDA build (default — bundles cublas/cudart 12.x DLLs, ~930 MB deployed)
scripts\update-crispasr.ps1 -Tag v0.8.30

# Or the Vulkan / CPU variants
scripts\update-crispasr.ps1 -Tag v0.8.30 -Asset crispasr-windows-x86_64-vulkan.zip
```

The script downloads the asset via `gh`, stops any running crispasr server (WhisperInk respawns on next dictation), backs up the current `crispasr.exe` + `*.dll` to `cohere-gguf\.old-<stamp>\`, swaps in the new binaries (GGUFs untouched), and smoke-tests `--help` — empty output is the `STATUS_DLL_NOT_FOUND` signature and triggers an automatic restore from the backup. Keep the script **pure ASCII**: PowerShell 5.1 reads BOM-less files as ANSI, and multi-byte punctuation decodes into stray smart-quote bytes that break the parser.

A CUDA build with global backend `auto` lights up NVIDIA GPUs automatically (`init_best`: CUDA > Vulkan > CPU); presets pinned `LocalGpuBackend: "cpu"` still run CPU via `-ng`. v0.7 also brings server-mode hotwords/beam-search fields (wired into `CrispAsrServerTranscriber`), auto-warmup at server start, and a `/load` model-hot-swap endpoint (unused so far). Upstream has no CLAUDE.md — its agent file is a 3-line `AGENTS.md`; the real docs are `README.md`, `docs/` (server.md, cli.md), `PERFORMANCE.md`, `ARCHITECTURE.md`.

**⚠️ CrispASR sync — desktop now runs the PREBUILT release `v0.8.30` (git sha `f632edf3`, built 2026-08-28), CUDA, deployed 2026-08-29** via `scripts\update-crispasr.ps1 -Tag v0.8.30`. Supersedes the local source build at pin `9eecfd43` (v0.7.1, 2026-06-13) — that was ~3190 commits behind. Four things changed: **(1)** This is the first deploy that is a *release artifact* rather than a local build, so **there are no local patches on the deployed binary at all**; the ggml-blas PkgConfig-optional patch only ever mattered for source builds and still sits in the sibling clone for that path. **(2)** v0.8.30 exists because **every official Windows CUDA build through v0.8.29 was unrunnable** — `build-windows-cuda` left `GGML_NATIVE=ON` so ggml compiled `-march=native` against an AVX-512 runner and died with `SIGILL` on any CPU without it (#374, nine of eleven GPU jobs affected). The v0.8.30 artifact is built portable and ships `cuda archs : 60/61/70/75/86/89/120` — **sm_86 covers the 3090s and the 3080**. Do not deploy an earlier GPU release. **(3)** The release headline — every non-16 kHz input was aliased by linear-interpolation resampling (`ac4aa478`) — **does not affect WhisperInk**: `MicCapture` captures 16 kHz mono, and the fix only fires when the file rate differs from the backend rate. It matters for anything else fed to crispasr by hand. **(4)** Backend count went 76 → 109; `qwen3`/`qwen3-1.7b` were already in the old binary, so the new `qwen3-asr-1.7b-local` preset did not depend on this update. **A/B before accepting** (2026-08-29, `jfk.wav`, warm, **server** mode, one harness run against both binaries back-to-back — old restored into a temp dir from its backup): cohere q6_k median **381 → 402 ms** (+5.5 %, inside run-to-run spread) and parakeet-rnnt + `--punc-model fullstop` median **749 → 325 ms (2.3× faster)**; transcripts byte-identical on both presets. **No regression, one large win.** (The ~317–352 ms figure recorded for the old build in the 2026-06-13 entry came from a different harness — measuring the *same* old binary here gave 381 ms, so compare the two columns above, not across dates.) The v0.7.1 binary is backed up in `cohere-gguf\.old-2026-08-29-1706\` for rollback (copy the 7 files back over the deploy dir). *History:* v0.7.1 originally regressed cohere-CUDA ~10× / CPU ~2× vs the 2026-05-03 build — a `beam_size=5` default whose cohere beam search snapshotted the KV cache through host memory ([#161](https://github.com/CrispStrobe/CrispASR/issues/161), fixed upstream by `4b27392f`); `f1b5e546` then made **greedy the server default**, so `LocalBeamSize: null` means greedy and the per-preset beam values are redundant. Older sets archived in `.old-2026-06-12-1354\`, `.old-2026-06-13-1602\`, `.v0.7.1-cuda-regressed\`. The laptop still runs a pre-v0.7 binary. **Still A/B any newer release against the deployed one before switching** — `main` keeps moving and the #374 episode shows release artifacts can ship broken.

**Truecase (§166) — wired but OFF by default.** `ApiProvider.LocalTruecaseModel` → crispasr `--truecase-model` (a resident startup post-processor parallel to `LocalPuncModel`, applied after punctuation). The plumbing is validated end-to-end on the deployed binary, but the §166 truecase models are low-quality on test audio: `auto`/`crf` **over-capitalize** content words (`fullstop`+`auto` → *"my **Fellow** americans ask **Not** what **Your Country Can**…"*; `crf` gets proper nouns but still over-caps) and `lstm` is a **no-op** (identical to punc-only). So no preset sets it — revisit when the upstream truecase checkpoints improve. The sibling companion `ApiProvider.LocalExtraParams` (per-provider `Dictionary<string,string>`) reaches the other §166 per-request fields (`punctuation`, `vad`, `seed`, `suppress_nst`, …) config-only, no recompile. Both fields parse + backfill in `LoadConfig` and copy in the `ProviderSettingsWindow` clone like the other `Local*` fields. (Bonus fix landed with this sync: `LocalPuncModel` now actually *parses* from `config.json` — previously it only got its value via default-backfill.) Build target remains **`crispasr-cli`** (not `whisper-cli`). **Deferred next phase:** WebSocket realtime dictation via the new `--ws-port` endpoint (whisper-only today → needs a whisper-GGUF provider + a streaming float32 mic path + partials UI; re-introduces the realtime mode removed 2026-06-13). **Dropped for now:** translate (revisit when mature — needs either granite AST or a ~502 MB m2m100 model + second server).

### CrispASR native build (alternative: from source)

`scripts/build-crispasr.ps1` clones `CrispStrobe/CrispASR` as a sibling of this repo (resolving via `$PSCommandPath` so no hardcoded paths), configures with the VS2022 generator, builds the `whisper-cli` target (which produces `crispasr.exe` via `OUTPUT_NAME`), and deploys the binary + all `*.dll` files to `%APPDATA%\.WhisperInk\cohere-gguf\`.

Key flags baked into the script:
- `-G "Visual Studio 17 2022" -A x64` — newer generators fail on Build Tools 2022 only installs
- `-DGGML_CUDA=OFF` — flip to `ON` for GPU builds (requires CUDA Toolkit 12.x)
- `-DWHISPER_BUILD_TESTS=OFF` — skips the Catch2 FetchContent that fails on offline/firewalled networks
- `--target whisper-cli` — the unified multi-backend CLI; `whisper-server` is a separate legacy target and **not** what WhisperInk uses (server mode is built into `crispasr.exe` via the `--server` subcommand)

Deploy copies **every** `*.dll` from `build\bin\Release\`, not just `ggml*`. Missing any of them causes `STATUS_DLL_NOT_FOUND` (exit code `-1073741515` / `0xC0000135`), which presents as a silent exit with no output — so if a build "runs but returns empty transcripts" or "`--help` prints nothing", check the DLL set first. **The set shrank between v0.7.1 and v0.8.30:** upstream folded the per-backend libraries (`parakeet.dll`, `canary.dll`, `cohere.dll`, `qwen3_asr.dll`, `voxtral*.dll`, `granite_speech.dll`, …) into `crispasr.dll`, so a current deploy is just `crispasr`, `whisper`, `ggml{,-base,-cpu,-cuda}` plus `cublas64_12` / `cublasLt64_12` / `cudart64_12` on CUDA. Older builds still need all 13. Either way the rule is the same — copy every `*.dll`, don't cherry-pick.

## Adding a provider

Four cases in ascending order of work. **Identify which one you are in before editing anything** — three of the four need no C# at all, and the recurring time-sink is assuming a new provider means new code.

| You want to add… | Work |
|---|---|
| **A.** Another GGUF that CrispASR already supports | **Config only.** One `CreateDefaults()` entry (or a hand-written `config.json` entry). No recompile. |
| **B.** A cloud API that speaks OpenAI multipart | **Config only.** Same, leaving `TranscriberKind` at its `Http` default. |
| **C.** A cloud API with its own protocol (Deepgram, Modulate, Smallest.ai, Reson8, Soniox, Chirp 3) | 4 edits: enum value → `ITranscriber` class → one factory line → preset. |
| **D.** A new *knob* on providers you already have | 3 **mandatory** edits — see [Adding a field](#adding-a-field-to-apiprovider). Orthogonal to A–C. |

**What you never have to touch.** `HealthProbe` and `ProviderDiagnostics` both dispatch on `TranscriberKind`, never on provider id — deliberately, because id-keyed dispatch used to flag every unlisted local provider as "Missing API key" and forced dummy keys to clear the banner. Any new provider is picked up automatically. `InferKindFromLegacyId` is **also not required** for a new preset: it only fires when a `config.json` entry carries no parseable `TranscriberKind`, and `CreateDefaults()` always writes one. Listing a new shipped id there is harmless insurance for hand-written entries — not a step you can forget and break.

### Local server ports

`CrispAsrServerTranscriber` spawns one `crispasr.exe --server` per preset, so each local preset owns a port. **Next free: 8113.**

| Port | Preset |
|---|---|
| 8103 | `parakeet-local` |
| 8105 | `cohere-local-q6k` |
| 8106 | `voxtral-local` |
| 8107 | `granite-local` |
| 8108 | `voxtral4b-local` |
| 8109 | `parakeet-rnnt-local` |
| 8112 | `qwen3-asr-1.7b-local` |
| 8766 | `cohere-gguf-server` (CPU-pinned) |
| ~~8102~~ | retired — old user-managed `qwen3-asr` Http preset (2026-08-29) |
| ~~8104 / 8767 / 8768~~ | retired — Q4 / cuda / cuda-q8 cohere presets (2026-06-12) |
| ~~8110~~ | used by the canary example below |
| ~~8111~~ | retired — lfm2-audio trial |

Retired ids still resolve in `InferKindFromLegacyId` / `HealthProbe` / `ProviderDiagnostics` so old configs keep working. They were pulled from `CreateDefaults()` because **the additive default-merge in `LoadConfig` resurrects any shipped default the user deletes, on every launch** — so removing a preset for real means deleting it from `CreateDefaults()`, not just from `config.json` (do both, or it comes straight back).

### A. Another CrispASR GGUF

1. **Check the backend is compiled in** before anything else — one command settles the whole question:
   ```powershell
   & "$env:APPDATA\.WhisperInk\cohere-gguf\crispasr.exe" --list-backends
   ```
   The `--backend` names are the left column. A name here means the deployed binary can run it *today*; a name that appears only in upstream's docs does not.
2. Drop the GGUF in `%APPDATA%\.WhisperInk\cohere-gguf\` (or a sibling folder, then set `LocalModelFolder`).
3. Add the entry. Shipped-to-everyone goes in `AppConfig.CreateDefaults()`; machine-local goes straight into `config.json`:
   ```json
   {
     "Id": "canary-local",
     "Name": "Canary Local (CrispASR)",
     "BaseUrl": "http://localhost:8110",
     "TranscriptionEndpoint": "http://localhost:8110/v1/audio/transcriptions",
     "TranscriberKind": "LocalCrispAsrServer",
     "LocalServerPort": 8110,
     "LocalModelGlob": "canary-*.gguf",
     "LocalBackendHint": "canary",
     "BiasMechanism": "none",
     "Language": "en"
   }
   ```
4. Restart WhisperInk. It appears under 🔌 Provider and the server lazy-spawns on first dictation.

**The fields that actually bite:**

- **`LocalModelGlob` — pin it to the model, not the family.** Presets share `cohere-gguf\` and `ResolveModel` takes the first `EnumerateFiles` match, so a loose glob silently hijacks a sibling preset. `parakeet-*.gguf` matches both Parakeet presets and "rnnt" sorts before "tdt", so they are pinned `parakeet-tdt-*` / `parakeet-rnnt-1.1b-*`; `qwen3-asr-1.7b-*` is pinned the same way against a future 0.6b drop-in. This fails **silently** and presents as a model-quality problem, not a config error.
- **`LocalBackendHint`** — only needed when GGUF metadata doesn't auto-detect. Cohere needs `"cohere"`, Voxtral 3B `"voxtral"`, Voxtral 4B `"voxtral4b"`, Granite `"granite"`. Qwen3-ASR *does* auto-detect (to `qwen3`) but its preset pins `"qwen3-1.7b"` to document intent. When unsure, pin it: it costs nothing and survives upstream detection changes.
- **`BiasMechanism`** — `"hotwords"` for CrispASR, but the name covers two unrelated mechanisms. On Parakeet it is a **CTC/TDT trie** (weak; garbles neighbours past boost ~8, so `HotwordsBoost` is off by default). On the speech-LLM backends (Voxtral 3B, Qwen3-ASR) it is a **decoder prompt-splice**, which is the only local biasing ever measured to reliably flip a hard term. Use `"none"` on Cohere/Granite/Voxtral-4B — they accept the field and ignore it.
- **`LocalPuncModel`** — only for backends with no native punctuation (Parakeet RNNT/CTC → `"fullstop"`). Speech-LLM backends punctuate and case themselves, so adding one just re-punctuates punctuated text. Leave `LocalTruecaseModel` unset (the §166 models over-capitalise).
- **`LocalGpuBackend`** — blank inherits the global `CrispGpuBackend`. Only pin `"cpu"` for a deliberate CPU-fallback preset.

**Verify before wiring it up.** Drive the server by hand with the *exact* command WhisperInk will spawn, so any failure is the model's and not the plumbing's:

```bash
crispasr.exe --server --host 127.0.0.1 --port <PORT> -m <MODEL.gguf> -t 8 -np --backend <HINT> --gpu-backend cuda
curl -s http://127.0.0.1:<PORT>/health
curl -s -F "file=@jfk.wav" http://127.0.0.1:<PORT>/v1/audio/transcriptions
curl -s -F "hotwords=termA,termB" -F "file=@clip.wav" http://127.0.0.1:<PORT>/v1/audio/transcriptions
```

`-t 8` is `Math.Min(8, ProcessorCount)`; `--gpu-backend` is passed whenever the resolved backend is neither `cpu` nor `auto` (`cpu` sends `-ng` instead). Health can take up to 120 s on a cold CUDA load — that ceiling is deliberate, since v0.7+ warms the model at startup. Sample audio: `..\CrispASR\samples\jfk.wav` plus the clinical clips in `_scratch\biasing\clips\`.

### B. Cloud API on OpenAI multipart

Same as A, but `TranscriberKind` stays `Http`. Set `BaseUrl`, `TranscriptionEndpoint` (if it isn't `/v1/audio/transcriptions`), `AuthHeaderName` (blank = `Bearer`), `ModelFieldName` (`model` vs `model_id`) and `TranscriptionModel`. `HttpTranscriber` already carries the per-provider quirks. **String fields always go before the `file` part** — Cohere v2 rejects the other order, and assume a new API might too.

### C. Cloud API with its own protocol

Only when the API is genuinely not OpenAI-shaped: raw-body POST, query-param options, a job/poll cycle, non-`Bearer` auth, or a nested response. Four edits:

1. `TranscriberKind` — new enum value in `AppConfig.cs`.
2. `<Name>Transcriber.cs` — implement `ITranscriber.TranscribeAsync(byte[], IReadOnlyList<string>, CancellationToken)`. Copy the closest existing one: `DeepgramTranscriber` / `SmallestTranscriber` / `Reson8Transcriber` (one-shot raw body + query params), `ModulateTranscriber` (one-shot multipart, endpoint-as-model), `SonioxTranscriber` (async upload → create → poll → fetch → delete).

**Probe the live API before trusting its reference doc.** Both recent Case-C additions found behaviour the published docs did not describe, and in each case the doc would have shipped a broken provider: Smallest.ai's `language` reference lists 26 of its 46 codes, calls the parameter required when Pulse Pro accepts its omission, and says nothing about `LANGUAGE_NOT_ENABLED_IN_REGION` — an entitlement 400 on a value that passes enum validation. Send real audio with real edge values (the house `auto` sentinel, a wrong model, a bad key) and read the actual error bodies; error *shapes* in particular tend to be undocumented and there are usually several per API.
3. `TranscriberFactory.Create` — one arm in the switch.
4. `CreateDefaults()` — the preset.

Route bias terms to the API's **native** field inside the transcriber and set `BiasMechanism` to a descriptive string — for these it is informational, since the transcriber does the routing. Two traps: the shared 15 s `_httpClient` timeout is per-client and cannot be raised per provider (see Future work), and its expiry arrives as `TaskCanceledException`, so a `catch (OperationCanceledException)` arm will mislabel a timeout as `cancelled`.

### Adding a field to `ApiProvider`

Three places, **all mandatory**. Miss one and the field misbehaves silently rather than failing loudly:

1. **`AppConfig.cs`** — the property and its default.
2. **`MainWindow.LoadConfig`** — an explicit `TryGetProperty("<Name>", …)` parse block. Without it the field never round-trips from `config.json` and only ever holds its default-backfill value. This bit `LocalPuncModel`, which appeared to work for weeks purely because its backfill happened to match the intended value.
3. **`ProviderSettingsWindow.CloneProvider`** — one assignment. The dialog edits a clone, so a missing line means **opening the settings dialog silently resets the field**. Deep-copy `Dictionary`/`List` fields (`new Dictionary<string,string>(src.X)`) rather than aliasing them.

All 29 current fields satisfy all three. Check a new one the same way:

```bash
grep -c 'TryGetProperty("YourField"' MainWindow.xaml.cs
grep -c "YourField = src.YourField" ProviderSettingsWindow.xaml.cs
```

## Common gotchas

- **Hook needs the window alive.** Closing the main window uninstalls the hook. Minimize, don't close.
- **`.NET 8 Desktop Runtime`** is required, not just the base runtime. The WPF assemblies live in the Desktop variant.
- **Mic enumeration is at launch.** Plug in before starting WhisperInk, or restart after plugging in.
- **Stuck modifier keys** should auto-clear via `ReleaseAllModifierKeys()`. If one persists, tapping the key once releases it — capture `debug.log` around the stuck event.
- **CrispASR silent failure = missing DLL.** Exit code `-1073741515` means `STATUS_DLL_NOT_FOUND`. Re-run `scripts\update-crispasr.ps1` (it deploys every `*.dll` from the release zip and auto-restores on a failed smoke test), or for source builds re-copy all `*.dll` from `build\bin\Release\`.
- **A local preset transcribes with the *wrong model*.** `LocalModelGlob` collided with a sibling preset — presets share `cohere-gguf\` and `ResolveModel` takes the first `EnumerateFiles` match. Fails silently and reads as a model-quality problem. Pin the glob to the model, not the family (see [Adding a provider](#adding-a-provider)).
- **A provider field resets every time the settings dialog opens.** Missing assignment in `ProviderSettingsWindow.CloneProvider` — the dialog edits a clone. Its sibling failure is a field that never loads from `config.json` at all, which is a missing `TryGetProperty` block in `LoadConfig`.
- **A deleted default provider comes back on every launch.** The default-merge in `LoadConfig` is additive and re-adds anything in `CreateDefaults()` the config lacks. Delete it from `CreateDefaults()` too, not just `config.json`.
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
- **The 15 s `_httpClient` timeout is hardcoded, shared, and its expiry is mislogged.** One `HttpClient` (`MainWindow.xaml.cs:73`) serves every cloud transcriber — `HttpTranscriber`, `Soniox`, `Deepgram`, `Modulate` — and `HttpClient.Timeout` is per-client, so no transcriber can raise its own budget; a per-request `CancellationTokenSource` could only shorten it. Two consequences: (1) a slow-but-healthy response past 15 s loses the dictation outright, with no retry and the audio already discarded; (2) the expiry surfaces as `TaskCanceledException`, which derives from `OperationCanceledException` — so the `catch (OperationCanceledException)` arm in `ModulateTranscriber`/`DeepgramTranscriber` logs it as **`cancelled`**, never as a timeout, and `debug.log` gives no way to tell a 15 s expiry from a real cancel. Fixing this properly means raising the shared client to a hard ceiling and giving each provider its own budget (a nullable `ApiProvider.RequestTimeoutSeconds`, default 15 to preserve today's behaviour) enforced per-request via a linked CTS — which touches all four transcribers, hence "future work" rather than a one-liner. Distinguishing timeout from cancel in the catch arms is independently worth doing and costs two lines each.
- CrispASR `/load` hot-swap could collapse the one-port-per-model preset scheme into a single resident server.
