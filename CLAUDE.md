# WhisperInk — CLAUDE.md

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Global hotkeys capture audio and transcribe via multiple ASR backends, typing or pasting results into the foreground application.

## Repository

- GitHub: `praxeo/whisperinc` (main branch)
- Language: C# / WPF
- Runtime: .NET 8.0 (Windows)
- NuGet deps: `NAudio 2.2.1`, `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.24.4`

## On-disk layout

Three directories are involved:

1. **Source — `Documents\GitHub\whisperinc\`** — this repo (C#/WPF app).
2. **Source — `Documents\GitHub\CrispASR\`** — sibling clone of the native ASR binary (C++/CMake). Built via `scripts/build-crispasr.ps1`. Can be absent if the user never runs local GGUF providers.
3. **Runtime — `%APPDATA%\.WhisperInk\`** — hardcoded deploy target. Contains:
   - `config.json`, `debug.log`, `history.json` — app state
   - `cohere-onnx\` — ONNX weights for `CohereOnnxTranscriber`
   - `cohere-gguf\` — `crispasr.exe` + all its DLLs + any `*.gguf` models for GGUF/Parakeet providers

The `%APPDATA%\.WhisperInk\cohere-gguf\` default folder is used by `CrispAsrServerTranscriber.cs`. Providers can override this per-entry via `ApiProvider.LocalModelFolder` (e.g. `"cohere-gguf-cuda"` for the CUDA preset) when the user keeps GGUFs in a different subdirectory.

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Global keyboard hook, recording state machine, transcription dispatch (now one factory call), tray context menu. ~2200 LOC. |
| `AppConfig.cs` | `ApiProvider` model, `TranscriberKind` enum, default provider list. New providers added via `CreateDefaults()` or directly in `config.json`. |
| `ITranscriber.cs` | Common interface every batch backend implements (`TranscribeAsync(byte[], biasTerms, ct)`). |
| `TranscriberFactory.cs` | Lazy-caches one `ITranscriber` per provider id. `Drop(id)` to free a single model; `DropAll()` after settings edits. |
| `HttpTranscriber.cs` | OpenAI-compatible multipart POST. Covers Mistral batch, OpenAI Whisper, Cohere v2 cloud, ElevenLabs Scribe v2 (auth/keyterms/tag_audio_events/no_verbatim quirks gated on provider fields), Qwen3-ASR, and any user-added cloud provider. |
| `CrispAsrServerTranscriber.cs` | Generic adapter for `crispasr.exe --server`. Reads port / model glob / backend hint / GPU backend / model folder from the `ApiProvider`. One class for Parakeet, Cohere, Voxtral, Granite, Canary, …  |
| `CohereOnnxTranscriber.cs` | In-process ONNX inference for Cohere Transcribe INT4/INT8 (encoder-decoder, 30s chunking, 5s overlap). |
| `GoogleChirp3Transcriber.cs` | Google Cloud STT v2 with OAuth + base64 JSON body and `adaptation.phraseSets` biasing. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for URLs, keys, auth header, model field, bias mode, Scribe keyterms. ElevenLabs-only fields hide for other providers. |
| `ContextBiasWindow.xaml(.cs)` | Global context-bias term list. |
| `PromptWindow.xaml(.cs)` | System-prompt editor for Ctrl+Alt AI mode. |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | Local transcript log + viewer. |

### Hotkeys

- **Ctrl+Space** — Hold to record, release to transcribe.
  - **Realtime** mode: WebSocket streaming to Mistral Voxtral proxy → live character-by-character typing via `WM_CHAR`/`PostMessage`.
  - **Batch** mode: Records WAV → HTTP POST to active provider → result pasted via clipboard (`Ctrl+V`).
- **Ctrl+Alt** — Hold to record a voice instruction. Grabs selected text from the foreground app, sends instruction + selection to the chat model, pastes response.

### Dictation modes

**Realtime** — Mistral-only. Requires a local WebSocket proxy at `ws://localhost:8765/v1/realtime`. Tunable `TargetStreamingDelayMs` (240–2400ms). Types each delta directly into the window that was in focus when recording started.

**Batch** — Works with every provider. Records to `~/Documents/MyRecordings/temp_audio.wav`, POSTs multipart form, optionally runs post-processing LLM correction, pastes with a leading space.

### Provider system

`ApiProvider` captures every knob; the cloud-vs-local split lives in `TranscriberKind`:

- **Shared HTTP/cloud knobs**: `BaseUrl`, `TranscriptionEndpoint` (override), `AuthHeaderName` (blank = Bearer, `xi-api-key` for ElevenLabs), `ModelFieldName` (`model` vs `model_id`), `TranscriptionModel`, `ChatModel`, `PostProcessModel`, `SupportsRealtime`, `SupportsTranscription`, `TranscriptionTemperature` (nullable)
- **Bias**: `ContextBiasMode`: `"none"` | `"whisper_prompt"` (OpenAI-compatible `prompt` field) | `"cohere_terms"` (JSON array)
- **ElevenLabs-only**: `ScribeKeytermsRaw` (newline-delimited, capped at 1000 terms / <50 chars / ≤5 words each), `TagAudioEvents`, `NoVerbatim`
- **TranscriberKind**: `Http` | `LocalOnnx` | `LocalCrispAsrServer` | `GoogleChirp3` — picks the `ITranscriber` implementation
- **LocalCrispAsrServer-only**: `LocalServerPort`, `LocalModelGlob` (e.g. `"parakeet-*.gguf"`), `LocalBackendHint` (e.g. `"cohere"` when auto-detect doesn't cover the GGUF), `LocalGpuBackend` (blank → fall back to global `CrispGpuBackend`), `LocalModelFolder` (blank → `cohere-gguf`)

Default providers (`AppConfig.CreateDefaults()`):

| Id | Name | TranscriberKind | Notes |
|----|------|-----------------|-------|
| `mistral` | Mistral | `Http` + realtime WS | Voxtral, realtime + batch |
| `openai` | OpenAI | `Http` | Whisper-1, `whisper_prompt` bias |
| `elevenlabs` | ElevenLabs Scribe | `Http` | `xi-api-key` auth, `model_id` field, keyterms |
| `cohere-api` | Cohere Transcribe API | `Http` | Cohere v2, temp 0.1, `cohere_terms` |
| `cohere-onnx` | Cohere Local (ONNX) | `LocalOnnx` | In-process; `CohereOnnxTranscriber` |
| `cohere-gguf-server` | Cohere Local (CrispASR, CPU) | `LocalCrispAsrServer` | Port 8766, `--backend cohere`, `cpu` |
| `cohere-gguf-cuda-server` | Cohere Local (CrispASR, CUDA) | `LocalCrispAsrServer` | Port 8767, `--backend cohere`, `cuda` |
| `cohere-gguf-cuda-server-q8` | Cohere Local (CrispASR, CUDA Q8) | `LocalCrispAsrServer` | Port 8768, Q8 GGUF |
| `qwen3-asr` | Qwen3-ASR Local | `Http` | Port 8102, user-managed external server |
| `parakeet-local` | Parakeet Local (CrispASR) | `LocalCrispAsrServer` | Port 8103, auto-detect backend |
| `cohere-local-q4` | Cohere Local Q4 (CrispASR) | `LocalCrispAsrServer` | Port 8104, Q4_K GGUF, `--backend cohere` |
| `cohere-local-q6k` | Cohere Local Q6_K (CrispASR) | `LocalCrispAsrServer` | Port 8105, Q6_K GGUF, near-F16 accuracy |
| `voxtral-local` | Voxtral Local (CrispASR) | `LocalCrispAsrServer` | Port 8106, `--backend voxtral`, prompt bias |
| `granite-local` | Granite Speech 4.1 Local (CrispASR) | `LocalCrispAsrServer` | Port 8107, `--backend granite`, prompt bias |
| `google-chirp3` | Google Chirp 3 | `GoogleChirp3` | OAuth + JSON body, native `phraseSets` biasing |

### Dispatch pipeline (`TranscriberFactory` + `ITranscriber`)

`MainWindow.xaml.cs:TranscribeAudioAsync` is a single factory lookup — no per-provider branching:

```csharp
var transcriber = _transcribers.GetOrCreate(provider);
if (!transcriber.IsReady(out var diag)) { Log(diag); return null; }
return await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms);
```

The factory caches one `ITranscriber` per provider id. Switching providers calls `Drop(oldId)` so we don't keep ~2 GB of resident model. Saving the settings dialog calls `DropAll()` because any field on any provider may have been edited.

### CrispAsrServerTranscriber (the generic one)

The path for every GGUF model. The constructor reads everything from the `ApiProvider`: port, model glob, backend hint, GPU backend (with fallback to the global `CrispGpuBackend`), model folder. First `TranscribeAsync` call lazy-spawns `crispasr.exe --server -m <model> --host 127.0.0.1 --port <port> -t <threads> -np [--backend X] [--gpu-backend Y]`, waits up to 45s for `/health`, and posts subsequent audio to `/v1/audio/transcriptions`. Server keeps model resident; process tree killed on `Dispose()`.

The legacy `CohereGgufTranscriber.cs`, `CohereGgufServerTranscriber.cs`, `CohereGgufCudaServerTranscriber.cs`, and `CohereGgufCudaQ8ServerTranscriber.cs` files have been deleted — the same providers now use this generic class via config-only entries.

### Local ONNX inference (CohereOnnxTranscriber)

- Encoder-decoder, 8 layers, 8 heads, 128 head dim, 16384 vocab
- Files in `%APPDATA%\.WhisperInk\cohere-onnx\`: `cohere-encoder.int4.onnx`, `cohere-decoder.int4.onnx`, `tokens.txt`
- INT4 from `cstr/cohere-transcribe-onnx-int4`; swap filenames for INT8
- 30s max chunk, 5s overlap, greedy autoregressive decoding
- CPU-only via `Microsoft.ML.OnnxRuntime.Gpu.Windows` (DirectML loaded but slow for autoregressive decoding; CUDA path blocked pending cuDNN for CUDA 13.0)

### Post-processing

Optional LLM correction pass (`PostProcessBatch` toggle). Uses `PostProcessModel` with a few-shot prompt that fixes garbled medical terms without over-correcting. Filters out commentary (e.g., "no correction needed").

### Text injection

- **Realtime**: `PostMessage(WM_CHAR, ch, 0)` per character to the target window handle captured at recording start. Bypasses IME and most focus-stealing issues.
- **Batch / AI**: `Clipboard.SetText` + synthetic `Ctrl+V`. Leading space prepended to avoid word-fusion.

### Keyboard hook internals

- `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` installed on the UI thread.
- Synthetic key presses (the paste `Ctrl+V`) tagged with sentinel `0x5AFE` in extra-info so the hook ignores its own injections.
- `ReleaseAllModifierKeys()` runs after every recording to clear any physical modifier still held when the hook fired — prevents "stuck Ctrl" after long sessions.

### Cohere v2 multipart quirk

Cohere v2 rejects requests where `file` appears before string fields. WhisperInk always appends strings (`model`, `language`, `temperature`, `context_bias_terms`, `keyterms`) **before** the `file` part.

### WebSocket serialization

All WebSocket sends go through `SemaphoreSlim(1,1)` so concurrent audio chunks + control frames can't interleave.

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

### CrispASR native build

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
     "BaseUrl": "http://localhost:8108",
     "TranscriberKind": "LocalCrispAsrServer",
     "LocalServerPort": 8108,
     "LocalModelGlob": "canary-*.gguf",
     "ContextBiasMode": "none",
     "Language": "en"
   }
   ```
   Set `LocalBackendHint` if the GGUF doesn't auto-detect (Cohere needs `"cohere"`, Voxtral `"voxtral"`, Granite `"granite"`).
3. Restart WhisperInk. The provider appears in the tray menu under 🔌 Provider, and `CrispAsrServerTranscriber` lazy-spawns the server on first dictation.

For a permanent default (shipped to every install), add the same entry to `AppConfig.CreateDefaults()` so new users get it on first run.

No C++ work needed — CrispASR auto-detects most backends from GGUF metadata.

## Common gotchas

- **Hook needs the window alive.** Closing the main window uninstalls the hook. Minimize, don't close.
- **`.NET 8 Desktop Runtime`** is required, not just the base runtime. The WPF assemblies live in the Desktop variant.
- **Mic enumeration is at launch.** Plug in before starting WhisperInk, or restart after plugging in.
- **Stuck modifier keys** should auto-clear via `ReleaseAllModifierKeys()`. If one persists, tapping the key once releases it — capture `debug.log` around the stuck event.
- **CrispASR silent failure = missing DLL.** Exit code `-1073741515` means `STATUS_DLL_NOT_FOUND`. Re-run the deploy step to copy all `*.dll` from `build\bin\Release\`.
- **Slow CPU inference on laptops = Windows power plan.** The default **Balanced** plan throttles CPU to base clock (e.g., 2.0 GHz on a Ryzen 5825U) even while plugged in, which roughly halves ASR throughput. Create and activate **Ultimate Performance**:
  ```powershell
  powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61
  powercfg /setactive 5898ace7-acb8-479d-b9c1-54af0f151d1b
  ```
  Symptom: RTFx that should be 2–3× is 1–1.5×; CPU stuck at `MaxClockSpeed` in `Get-CimInstance Win32_Processor`.
- **`update_automation` MCP tool** (home-automation side, unrelated but documented for completeness) is broken on the user's HA instance — YAML edits must be manual.

## Repo hygiene

`.gitignore` excludes `publish/`, `bin/`, `obj/`, `build.log`. The CrispASR source tree lives in a sibling directory, not a submodule — intentionally, so CrispASR updates are a plain `git pull` in that folder without dragging submodule plumbing into this repo.
