# WhisperInk

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Global hotkeys capture audio and transcribe via multiple ASR backends, typing or pasting results into the foreground application.

## Repository

- GitHub: `praxeo/dictation` (master branch)
- Language: C# / WPF
- Runtime: .NET 8.0 (Windows)
- NuGet deps: `NAudio 2.2.1`, `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.24.4`

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Global keyboard hook, recording state machine, all transcription/AI logic, context menu UI |
| `AppConfig.cs` | `ApiProvider` model (multi-provider config) and `AppConfig` defaults |
| `CohereOnnxTranscriber.cs` | Local ONNX inference for Cohere Transcribe INT4/INT8 (encoder-decoder, 30s chunking, overlap stitching) |
| `HistoryService.cs` / `HistoryWindow.xaml` | Transcription history log |
| `ProviderSettingsWindow.xaml` | GUI for adding/editing API providers |
| `PromptWindow.xaml` | System prompt editor (used for Ctrl+Alt AI mode) |

### Hotkeys

- **Ctrl+Space** — Hold to record, release to transcribe. Mode determines behavior:
  - **Realtime**: WebSocket streaming to Mistral Voxtral proxy → live character-by-character typing via `WM_CHAR`/`PostMessage`
  - **Batch**: Records WAV → HTTP POST to active provider → result pasted via clipboard (`Ctrl+V`)
- **Ctrl+Alt** — Hold to record for AI query mode. Grabs selected text as context, transcribes voice instruction, sends both to chat LLM, pastes response.

### Dictation Modes

**Realtime** — Mistral-only. Requires a local WebSocket proxy (`ws://localhost:8765/v1/realtime`). Configurable streaming delay (240–2400ms). Types each delta directly into the target window.

**Batch** — Works with all providers. Records to `~/Documents/MyRecordings/temp_audio.wav`, POSTs multipart form to provider's transcription endpoint, optionally runs post-processing LLM correction, then pastes result.

### Provider System

Multi-provider architecture defined in `ApiProvider`. Each provider configures:
- `BaseUrl`, `TranscriptionEndpoint` (override), `AuthHeaderName` (custom header vs Bearer), `ModelFieldName` ("model" vs "model_id")
- `TranscriptionModel`, `ChatModel`, `PostProcessModel`
- `SupportsRealtime`, `SupportsTranscription`
- `TranscriptionTemperature` (nullable double)
- `ContextBiasMode`: `"none"` | `"whisper_prompt"` (OpenAI `prompt` field) | `"cohere_terms"` (Cohere `context_bias_terms` JSON array)

Default providers (in `ApiProvider.CreateDefaults()`):

| Id | Name | Notes |
|----|------|-------|
| `mistral` | Mistral | Voxtral, supports realtime WebSocket |
| `openai` | OpenAI | Whisper-1, whisper_prompt bias |
| `elevenlabs` | ElevenLabs Scribe | Custom auth header `xi-api-key`, model field `model_id` |
| `cohere-api` | Cohere Transcribe API | Cohere v2 endpoint, temp 0.1, cohere_terms bias |
| `local` | Local Server | localhost:8100, whisper_prompt bias |
| `cohere-onnx` | Cohere Local (ONNX) | Bypasses HTTP entirely, runs CohereOnnxTranscriber |

### Local ONNX Inference (CohereOnnxTranscriber)

- Encoder-decoder architecture, 8 layers, 8 heads, 128 head dim, 16384 vocab
- Model files in `%APPDATA%\.WhisperInk\cohere-onnx\`: `cohere-encoder.int4.onnx`, `cohere-decoder.int4.onnx`, `tokens.txt`
- INT4 from `cstr/cohere-transcribe-onnx-int4` (HuggingFace); swap filenames for INT8
- 30s max chunk, 5s overlap, greedy autoregressive decoding
- CPU-only via `Microsoft.ML.OnnxRuntime.Gpu.Windows` (DirectML loaded but slow for autoregressive; CUDA blocked by missing cuDNN)

### Post-Processing

Optional LLM correction pass (`PostProcessBatch` toggle). Uses `PostProcessModel` with a few-shot prompt that fixes garbled medical terms without over-correcting valid English. Filters out commentary responses (e.g. "no correction needed").

### Text Input

- **Realtime**: `PostMessage(WM_CHAR)` per character to target window handle captured at recording start
- **Batch/AI**: Clipboard `SetText` + simulated `Ctrl+V`
- Leading space prepended to avoid merging with existing text

### Config

JSON at `%APPDATA%\.WhisperInk\config.json`. Stores providers array, active provider ID, dictation mode, microphone selection, sound toggle, system prompt, context bias terms, post-process settings, proxy path, streaming delay.

Debug log at `%APPDATA%\.WhisperInk\debug.log`.

## Build & Publish

```powershell
# Framework-dependent (requires .NET 8 runtime)
dotnet publish -c Release -r win-x64 --self-contained false

# Self-contained
dotnet publish -c Release -r win-x64 --self-contained true
```

## Key Implementation Notes

- Global low-level keyboard hook (`WH_KEYBOARD_LL`) with synthetic key marker (`0x5AFE`) to avoid re-entrant hook processing
- Target window (`GetForegroundWindow()`) captured before recording starts so text goes to the right window
- `ReleaseAllModifierKeys()` called after every recording to prevent stuck modifier keys
- Multipart form field order matters for Cohere v2: all string fields before the file part
- WebSocket send is serialized via `SemaphoreSlim` to prevent concurrent writes
- UI sounds are procedurally generated sine waves (no asset files)
