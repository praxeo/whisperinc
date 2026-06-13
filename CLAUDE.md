# WhisperInk — CLAUDE.md

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Global hotkeys capture audio and transcribe via multiple ASR backends, typing or pasting results into the foreground application.

## Repository

- GitHub: `praxeo/whisperinc` (main branch)
- Language: C# / WPF
- Runtime: .NET 8.0 (Windows)
- NuGet deps: `NAudio 2.2.1`, `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.24.4`

## On-disk layout

Three directories are involved:

1. **Source — `OneDrive\Desktop\whisperinc\`** — this repo (C#/WPF app). (On the desktop machine; path may differ per machine — resolve relative to the repo, never hardcode.)
2. **Source — `OneDrive\Desktop\CrispASR\`** — sibling clone of the native ASR binary (C++/CMake). Optional: since CrispASR v0.7 ships prebuilt Windows binaries, the normal update path is `scripts/update-crispasr.ps1` (see below) and the clone is only needed for source builds. It carries one uncommitted local patch (ggml-blas PkgConfig-optional fix).
3. **Runtime — `%APPDATA%\.WhisperInk\`** — hardcoded deploy target. Contains:
   - `config.json`, `debug.log`, `history.json` — app state
   - `cohere-onnx\` — ONNX weights for `CohereOnnxTranscriber`
   - `cohere-gguf\` — `crispasr.exe` + all its DLLs + any `*.gguf` models for GGUF/Parakeet providers

The `%APPDATA%\.WhisperInk\cohere-gguf\` default folder is used by `CrispAsrServerTranscriber.cs`. Providers can override this per-entry via `ApiProvider.LocalModelFolder` (e.g. `"cohere-gguf-cuda"` for the CUDA preset) when the user keeps GGUFs in a different subdirectory.

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Orchestration: recording state machine (`_recState` tri-state via `Interlocked`), transcription dispatch (one factory call), config load/save, the shared `BuildAppMenu()` tree. ~1750 LOC. |
| `KeyboardHookService.cs` | Owns the `WH_KEYBOARD_LL` hook: modifier tracking, hotkey suppression, synthetic-event filter (0x5AFE marker), Ctrl+Space detection. Callbacks fire on the hook thread; MainWindow marshals via Dispatcher + `RunSafe`. |
| `TextInjector.cs` | Synthetic text delivery: clipboard paste-with-restore (batch), selection grab via Ctrl+C, `ReleaseAllModifierKeys()`. Owns pending clipboard-restore state. |
| `MenuModel.cs` | `MenuNode` record + `MenuSurface` (Both/TrayOnly/BarOnly) + WPF renderer. One canonical menu tree drives both the tray menu and the bar right-click menu — they cannot drift. |
| `TrayIcon.cs` | `TrayIconManager`: notification-area icon + WinForms renderer over the shared `MenuNode` tree (rebuilt on every `Opening`). |
| `AppConfig.cs` | `ApiProvider` model, `TranscriberKind` enum, default provider list. New providers added via `CreateDefaults()` or directly in `config.json`. |
| `ITranscriber.cs` | Common interface every batch backend implements (`TranscribeAsync(byte[], biasTerms, ct)`). |
| `TranscriberFactory.cs` | Lazy-caches one `ITranscriber` per provider id. `Drop(id)` to free a single model; `DropAll()` after settings edits. |
| `HttpTranscriber.cs` | OpenAI-compatible multipart POST. Covers Mistral batch, OpenAI Whisper, Cohere v2 cloud, ElevenLabs Scribe v2 (auth/keyterms/tag_audio_events/no_verbatim quirks gated on provider fields), Qwen3-ASR, and any user-added cloud provider. |
| `CrispAsrServerTranscriber.cs` | Generic adapter for `crispasr.exe --server`. Reads port / model glob / backend hint / GPU backend / model folder from the `ApiProvider`. One class for Parakeet, Cohere, Voxtral, Granite, Canary, …  |
| `CohereOnnxTranscriber.cs` | In-process ONNX inference for Cohere Transcribe INT4/INT8 (encoder-decoder, 30s chunking, 5s overlap). |
| `GoogleChirp3Transcriber.cs` | Google Cloud STT v2 with OAuth + base64 JSON body and `adaptation.phraseSets` biasing. |
| `SonioxTranscriber.cs` | Soniox async REST job (`api.soniox.com/v1`): upload WAV → create transcription → poll status → fetch token array → concatenate → best-effort delete of job+file. Bias terms ride the `context.terms` field. Not OpenAI-compatible, so it bypasses `HttpTranscriber`. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for URLs, keys, auth header, model field, a read-only biasing-mechanism line, and the Parakeet hotword-boost / Scribe extras. ElevenLabs-only fields hide for other providers. |
| `ContextBiasWindow.xaml(.cs)` | Global context-bias term list. |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | Local transcript log + viewer. |

### Hotkeys

- **Ctrl+Space** — Hold to record, release to transcribe. Records WAV → HTTP POST to active provider → result pasted via clipboard (`Ctrl+V`).

### Dictation

Records to `~/Documents/MyRecordings/temp_audio.wav`, POSTs multipart form, pastes with a leading space. Works with every provider.

### Provider system

`ApiProvider` captures every knob; the cloud-vs-local split lives in `TranscriberKind`:

- **Shared HTTP/cloud knobs**: `BaseUrl`, `TranscriptionEndpoint` (override), `AuthHeaderName` (blank = Bearer, `xi-api-key` for ElevenLabs), `ModelFieldName` (`model` vs `model_id`), `TranscriptionModel`, `SupportsTranscription`, `TranscriptionTemperature` (nullable)
- **Bias**: one shared `AppConfig.ContextBiasTerms` list is routed to each provider's NATIVE field by `ApiProvider.BiasMechanism` (baked per provider in `CreateDefaults`; never user-set — `ResolvedBiasMechanism` falls back to the legacy `ContextBiasMode` for user-added providers). Mechanisms: `whisper_prompt` (labeled glossary in `prompt` — OpenAI, local shims), `mistral_context_bias` (comma string in `context_bias` — Mistral Voxtral batch, ≤100 terms), `elevenlabs_keyterms` (repeated `keyterms` from the shared list), `hotwords` (comma string — CrispASR local: real CTC/TDT/RNNT trie on Parakeet driven by `HotwordsBoost` ~10, prompt-injection on Voxtral-3B/Qwen3, accepted-but-no-op on Cohere/Granite/Voxtral-4B), `phrase_sets` / `context_terms` (Google / Soniox, handled natively in their transcribers), `none`. **Cohere Transcribe v2 has no biasing field** — its old `cohere_terms`→`context_bias_terms` was a phantom the server silently dropped, now removed.
- **ElevenLabs-only**: `TagAudioEvents`, `NoVerbatim`. Keyterms now come from the shared Context Bias list; `ScribeKeytermsRaw` remains an optional ElevenLabs-only supplement merged in (validated together: ≤1000 terms / <50 chars / ≤5 words, illegal chars dropped).
- **TranscriberKind**: `Http` | `LocalOnnx` | `LocalCrispAsrServer` | `GoogleChirp3` | `Soniox` — picks the `ITranscriber` implementation
- **LocalCrispAsrServer-only**: `LocalServerPort`, `LocalModelGlob` (e.g. `"parakeet-*.gguf"`), `LocalBackendHint` (e.g. `"cohere"` when auto-detect doesn't cover the GGUF), `LocalGpuBackend` (blank → fall back to global `CrispGpuBackend`), `LocalModelFolder` (blank → `cohere-gguf`), `LocalBeamSize` (nullable int → `beam_size` form field; null = greedy; needs CrispASR v0.7+; editable in provider settings), `LocalPuncModel` (server-side punctuation model → crispasr `--punc-model`: `"fullstop"`/`"auto"`/`"firered"`/`"punctuate-all"`; blank → none; restores punctuation + sentence case for non-PnC backends like Parakeet RNNT/CTC — **requires the local #161-punc CrispASR build**, see the regression note below)

Default providers (`AppConfig.CreateDefaults()`):

| Id | Name | TranscriberKind | Notes |
|----|------|-----------------|-------|
| `mistral` | Mistral | `Http` | Voxtral batch |
| `openai` | OpenAI | `Http` | Whisper-1, `whisper_prompt` bias |
| `elevenlabs` | ElevenLabs Scribe | `Http` | `xi-api-key` auth, `model_id` field, keyterms |
| `cohere-api` | Cohere Transcribe API | `Http` | Cohere v2, temp 0.1; no native biasing |
| `cohere-onnx` | Cohere Local (ONNX) | `LocalOnnx` | In-process; `CohereOnnxTranscriber` |
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

### Dispatch pipeline (`TranscriberFactory` + `ITranscriber`)

`MainWindow.xaml.cs:TranscribeAudioAsync` is a single factory lookup — no per-provider branching:

```csharp
var transcriber = _transcribers.GetOrCreate(provider);
if (!transcriber.IsReady(out var diag)) { Log(diag); return null; }
return await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms);
```

The factory caches one `ITranscriber` per provider id. Switching providers calls `Drop(oldId)` so we don't keep ~2 GB of resident model. Saving the settings dialog calls `DropAll()` because any field on any provider may have been edited.

### CrispAsrServerTranscriber (the generic one)

The path for every GGUF model. The constructor reads everything from the `ApiProvider`: port, model glob, backend hint, GPU backend (with fallback to the global `CrispGpuBackend`), model folder. First `TranscribeAsync` call lazy-spawns `crispasr.exe --server -m <model> --host 127.0.0.1 --port <port> -t <threads> -np [--backend X] [--punc-model FNAME]` (plus `-ng` when the effective backend is `cpu`, or `--gpu-backend X` when it's a specific GPU; `auto` passes nothing so ggml's `init_best` picks CUDA > Vulkan > CPU per what the binary was built with), waits up to **120s** for `/health` (v0.7 auto-warms the model in server mode, so first health on a CUDA build includes VRAM upload + warmup), and posts subsequent audio to `/v1/audio/transcriptions` with `language`, `hotwords` (+ `hotwords_boost` when `HotwordsBoost` is set) whenever bias terms exist, and optional `beam_size`. The OpenAI `prompt` field is NOT sent — no CrispASR backend reads it (Voxtral/Qwen3 splice `hotwords` into their prompt themselves). Thread count is capped at `Min(8, ProcessorCount)` deliberately: ggml ASR scales with physical cores/memory bandwidth, not SMT, and `-t` barely matters on GPU backends. Server keeps model resident; process tree killed on `Dispose()`. When `LocalPuncModel` is set, the spawn adds `--punc-model <model>` so the resident server restores punctuation per segment (FireRedPunc) — stock CrispASR applied `--punc-model` only in CLI one-shot mode, so this requires the local #161-punc build (see the regression note).

The legacy `CohereGgufTranscriber.cs`, `CohereGgufServerTranscriber.cs`, `CohereGgufCudaServerTranscriber.cs`, and `CohereGgufCudaQ8ServerTranscriber.cs` files have been deleted — the same providers now use this generic class via config-only entries.

### Local ONNX inference (CohereOnnxTranscriber)

- Encoder-decoder, 8 layers, 8 heads, 128 head dim, 16384 vocab
- Files in `%APPDATA%\.WhisperInk\cohere-onnx\`: `cohere-encoder.int4.onnx`, `cohere-decoder.int4.onnx`, `tokens.txt`
- INT4 from `cstr/cohere-transcribe-onnx-int4`; swap filenames for INT8
- 30s max chunk, 5s overlap, greedy autoregressive decoding
- CPU-only via `Microsoft.ML.OnnxRuntime.Gpu.Windows` (DirectML loaded but slow for autoregressive decoding; CUDA path blocked pending cuDNN for CUDA 13.0)

### Soniox async REST (SonioxTranscriber)

Soniox has no synchronous "POST audio → text" endpoint, so each dictation is a four-step job on `api.soniox.com/v1` (Bearer auth): `POST /files` (multipart) → `POST /transcriptions` (JSON: `model`, `file_id`, `language_hints`, optional `context.terms`) → poll `GET /transcriptions/{id}` until `status` is `completed`/`error` → `GET /transcriptions/{id}/transcript`. The transcript is a `tokens[]` array whose text carries its own leading spacing, so concatenation rebuilds the sentence (a flat `text` field is a fallback). Polling is every 400 ms with a 120 s ceiling; each request is bounded by the shared 15 s `HttpClient` timeout. A `finally` block best-effort `DELETE`s both the transcription and the uploaded file (even on cancel/error) so nothing accumulates under the key. Default model `stt-async-v5` (current GA as of 2026-06-11; `stt-async-v4` is deprecated, removed 2026-06-30) is user-editable, so a Soniox model rename is a config edit, not a recompile. Context-bias terms map straight onto `context.terms` (real vocabulary steering, capped at 100), so `BiasMechanism` is informational only — same approach as Google Chirp 3.

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

Start/stop chirps are procedurally generated sine waves. No asset files.

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

**⚠️ v0.7.x performance regression — desktop now runs the local CUDA build of the fix (`main` @ `4b27392f`), deployed 2026-06-12.** History: v0.7.1 regressed cohere-CUDA ~10× and CPU ~2× vs the 2026-05-03 build (8.4s clip, q6_k cohere, warm: May CUDA **0.4s** / CPU 2.7s vs v0.7.1 4.6s / 6.0s / Vulkan 27s+). Tracked upstream as [CrispASR#161](https://github.com/CrispStrobe/CrispASR/issues/161) (filed 2026-06-10, still open — no release with the fix yet). Root cause (confirmed 2026-06-11): v0.7 defaulted **`beam_size=5` for every backend** in CLI *and* server mode, and cohere's beam search snapshotted the KV cache through host memory per beam per step — `4b27392f` keeps snapshots on-device (warm q6_k CUDA: beam-5 0.85–0.94s, `-bs 1` greedy **0.31s**, beating the May build). Parakeet's slice was purely the beam-5 default (TDT beam ≈4.5× cost; `beam_size=1` restores 0.8s). The verified `4b27392f` build (from `CrispASR\build-161\bin\Release\`) is deployed in `cohere-gguf\`; the May-3 set is backed up in `.old-2026-06-12-1354\` and v0.7.1 stays archived in `.v0.7.1-cuda-regressed\`. Desktop config.json sets `LocalBeamSize: 1` on the six cohere/parakeet presets (null = *server-default beam 5*, not greedy, on v0.7+); voxtral/granite left at server default. The `hotwords`/`beam_size` fields WhisperInk sends are now actually honored on the desktop. The laptop still runs a pre-v0.7 binary. Upstream pushed a further "beam wiring" fix to `main` on 2026-06-12 (re: the beam-default question) — unverified; A/B any newer build or release against `4b27392f` before switching (watch #161).

**⚠️ The deployed `cohere-gguf\` binary now carries a LOCAL patch on top of `4b27392f`: server-mode punctuation (#161-punc), rebuilt & redeployed 2026-06-12.** Stock CrispASR applies `--punc-model` only in CLI one-shot mode; the server (`examples/cli/crispasr_server.cpp`) ignored the flag, so non-PnC backends (Parakeet RNNT/CTC) returned lowercase, unpunctuated text through WhisperInk's server path. The patch is ~6 additive edits to `crispasr_server.cpp`: load a resident `fireredpunc_context` at server start when `--punc-model` is set (same alias resolver the CLI uses — `auto`/`firered`/`fullstop`/`punctuate-all`, auto-downloads to `~/.cache/crispasr`), thread it into `do_transcribe`, and apply `fireredpunc_process` per segment after the existing punctuation-strip block (serialized on a private mutex). Verified: RNNT+`fullstop` now returns `"The patient presents … shortness of breath. Vitals are stable. … oxygen?"` via `/v1/audio/transcriptions`; warm **≈1.1s at `beam_size=1`** (the punc pass adds only ~100 ms; the ~4s seen with no `beam_size` is RNNT beam-5, not punctuation). The patch lives in the sibling `CrispASR` clone as an **uncommitted local change — NOT upstream**, so it must be re-applied after any CrispASR pull/update (same status as the ggml-blas patch). Pre-patch `crispasr.exe`+`crispasr.dll` backed up in `cohere-gguf\.pre-punc-2026-06-12\`. WhisperInk drives it via `ApiProvider.LocalPuncModel` (`parakeet-rnnt-local` → `"fullstop"`); the field is backfilled in `LoadConfig` like the other `Local*` fields. Build target is **`crispasr-cli`** (not `whisper-cli`). **UPDATE 2026-06-13 — landed upstream:** the maintainer cherry-picked the patch into `CrispStrobe/CrispASR` `main` as `36f35f2a` (PR #166, now closed) and shipped the full §166 parity series on top (PCS + CTC auto-enable + shared resolver, `crispasr_session_set_punc_model` + Python/Go/Dart wrappers, WebSocket streaming, `/v1/translate`). The deployed `4b27392f`+patch binary is unchanged and still needs the patch re-applied on any rebuild — but once a vetted upstream build containing `36f35f2a` is adopted (A/B per the #161 rule first; `main` has moved a lot), the local patch can be dropped.

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
