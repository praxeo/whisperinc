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

The `%APPDATA%\.WhisperInk\cohere-gguf\` path is **hardcoded** in `CrispAsrServerTranscriber.cs` and the `CohereGguf*Transcriber.cs` siblings. If that hardcode ever needs to change, it's a single-line `Path.Combine(...ApplicationData..., ".WhisperInk", "cohere-gguf")` in each.

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Global keyboard hook, recording state machine, dispatch to every transcriber, context menu UI. ~1800 LOC. |
| `AppConfig.cs` | `ApiProvider` model + `AppConfig` defaults. Edit `CreateDefaults()` to add new providers. |
| `CohereOnnxTranscriber.cs` | In-process ONNX inference for Cohere Transcribe INT4/INT8 (encoder-decoder, 30s chunking, 5s overlap). |
| `CohereGgufTranscriber.cs` | One-shot `crispasr.exe` subprocess per recording. Cohere-only. |
| `CohereGgufServerTranscriber.cs` | Persistent `crispasr.exe --server` on CPU. Cohere-only, predates the unified server path. |
| `CohereGgufCudaServerTranscriber.cs` | Same but CUDA. Cohere-only. |
| `CohereGgufCudaQ8ServerTranscriber.cs` | CUDA Q8 variant with `cohere_terms` context biasing. |
| `CrispAsrServerTranscriber.cs` | **Generic** server adapter. Auto-detects backend from GGUF metadata, so one class serves Parakeet/Canary/Voxtral/Qwen3 without per-model subclasses. New models should use this, not a new Cohere-style wrapper. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for URLs, keys, auth header, model field, bias mode, Scribe keyterms. |
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

`ApiProvider` captures every knob:

- `BaseUrl`, `TranscriptionEndpoint` (override), `AuthHeaderName` (blank = Bearer, `xi-api-key` for ElevenLabs), `ModelFieldName` (`model` vs `model_id`)
- `TranscriptionModel`, `ChatModel`, `PostProcessModel`
- `SupportsRealtime`, `SupportsTranscription`
- `TranscriptionTemperature` (nullable)
- `ContextBiasMode`: `"none"` | `"whisper_prompt"` (OpenAI-compatible `prompt` field) | `"cohere_terms"` (JSON array)
- `ScribeKeytermsRaw` — newline-delimited keyterms for ElevenLabs Scribe v2 (repeated `keyterms` multipart fields, capped at 1000 terms / <50 chars / ≤5 words each)

Default providers (`AppConfig.CreateDefaults()`):

| Id | Name | Transport | Notes |
|----|------|-----------|-------|
| `mistral` | Mistral | HTTPS + WS | Voxtral, realtime + batch |
| `openai` | OpenAI | HTTPS | Whisper-1, `whisper_prompt` bias |
| `elevenlabs` | ElevenLabs Scribe | HTTPS | `xi-api-key` auth, `model_id` field, keyterms |
| `cohere-api` | Cohere Transcribe API | HTTPS | Cohere v2, temp 0.1, `cohere_terms` |
| `local` | Local Server | HTTP | `localhost:8100`, `whisper_prompt` |
| `cohere-onnx` | Cohere Local (ONNX) | in-process | Bypasses HTTP; `CohereOnnxTranscriber` |
| `cohere-gguf` | Cohere Local (CrispASR GGUF) | subprocess | llama.cpp CLI, one-shot per call |
| `cohere-gguf-server` | Cohere Local (CrispASR server) | HTTP 8766 | Persistent CPU server |
| `cohere-gguf-cuda-server` | Cohere Local (CrispASR CUDA) | HTTP | Persistent CUDA server |
| `cohere-gguf-cuda-server-q8` | Cohere Local (CrispASR CUDA Q8) | HTTP | CUDA Q8 + `cohere_terms` |
| `qwen3-asr` | Qwen3-ASR Local | HTTP 8102 | OpenAI-compatible |
| `parakeet-local` | Parakeet Local (CrispASR) | HTTP 8103 | Auto-spawned via `CrispAsrServerTranscriber` |
| `cohere-local-q4` | Cohere Local Q4 (CrispASR) | HTTP 8104 | Auto-spawned, Q4_K GGUF, explicit `--backend cohere` |

### CrispAsrServerTranscriber (the generic one)

The clean path for any future GGUF model. Construct with a model glob (`"parakeet-*.gguf"`, `"canary-*.gguf"`, etc.) and a port; first `TranscribeAsync` call lazy-spawns `crispasr.exe --server -m <model> --host 127.0.0.1 --port <port> -t <threads> -np`, waits up to 45s for `/health`, and posts subsequent audio to `/v1/audio/transcriptions`. Server keeps model resident; process tree killed on `Dispose()`.

An optional `backendHint` parameter appends `--backend <hint>` to the spawn args. Cohere GGUFs need `backendHint: "cohere"` because they don't expose backend metadata for auto-detect. Parakeet and the other newer backends auto-detect correctly and leave it null.

`MainWindow.xaml.cs` owns one field per auto-spawned model (`_parakeetServer`, `_cohereQ4Server`). Disposal chains are handled in the main window's `OnClosed`.

The `CohereGguf*Transcriber` classes predate this unified class and should be considered legacy — new models should add a provider entry and reuse `CrispAsrServerTranscriber` rather than subclassing.

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

1. Download the GGUF into `%APPDATA%\.WhisperInk\cohere-gguf\`.
2. Add an `ApiProvider` entry in `AppConfig.CreateDefaults()` with a unique `Id` (e.g., `"canary-local"`).
3. In `MainWindow.xaml.cs`, add a field `private CrispAsrServerTranscriber? _canaryServer;`, an `IsCanaryLocalProvider` helper, and a dispatch block in the transcription path that constructs `new CrispAsrServerTranscriber(modelGlob: "canary-*.gguf", port: <pick_a_port>, displayName: "Canary")`.
4. Add disposal to `OnClosed`.
5. If you want user-configurable port/model selection, add it to `ProviderSettingsWindow`.

No C++ work needed — CrispASR auto-detects the backend from GGUF metadata.

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
