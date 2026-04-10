# WhisperInk

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Global hotkeys capture audio and transcribe via multiple ASR backends, typing or pasting results into the foreground application.

## Features

- **Global Hotkeys**: Press and hold `Ctrl+Space` to record, release to transcribe
- **Multiple ASR Backends**: Support for Mistral, OpenAI, ElevenLabs, Cohere API, and local ONNX inference
- **Realtime Streaming**: Live character-by-character typing with Mistral Voxtral (via WebSocket proxy)
- **Batch Transcription**: Record audio file and transcribe via HTTP POST
- **AI Query Mode**: `Ctrl+Alt` to record voice instructions with selected text context
- **Post-Processing**: Optional LLM correction for medical/technical terminology
- **Local ONNX Support**: Run Cohere Transcribe models locally with CPU/GPU acceleration

## Repository

- **GitHub**: https://github.com/praxeo/whisperinc
- **Language**: C# / WPF
- **Runtime**: .NET 8.0 (Windows)
- **NuGet Dependencies**: `NAudio 2.2.1`, `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.24.4`

## Quick Start

### Windows Application

1. **Download the latest release** or build from source:
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true
   ```

2. **Run WhisperInk.exe** — The app will start in your system tray

3. **Configure API Keys**:
   - Right-click the tray icon
   - Select "API Providers"
   - Add your API keys for your chosen providers (Mistral, OpenAI, ElevenLabs, Cohere, etc.)

4. **Start Dictating**:
   - Hold `Ctrl+Space` to record
   - Release to transcribe and paste text

### Backend Servers

WhisperInk can work with optional backend servers for enhanced functionality:

#### 1. Cohere Transcribe Server (FastAPI)

A local FastAPI server for running Cohere Transcribe models with GPU acceleration.

**Requirements**:
- Python 3.10+
- CUDA-capable GPU (NVIDIA) for best performance
- 16GB+ RAM recommended

**Setup**:

```bash
# Navigate to server directory
cd server

# Create virtual environment
python -m venv venv

# Activate virtual environment (Windows)
venv\Scripts\activate

# Install dependencies
pip install transformers>=4.52.0 torch soundfile fastapi uvicorn
```

**Run the Server**:

```bash
# Activate virtual environment
venv\Scripts\activate

# Start the server
python cohere_server.py
```

The server will start on `http://127.0.0.1:8101`

**Configure WhisperInk**:

1. Right-click the WhisperInk tray icon
2. Select "API Providers"
3. Add a new provider with:
   - **Name**: `Cohere Local`
   - **Base URL**: `http://127.0.0.1:8101`
   - **Transcription Endpoint**: `/v1/audio/transcriptions`
   - **API Key**: (leave blank for local server)
   - **Transcription Model**: `cohere-transcribe-03-2026`

**Server Features**:
- CUDA acceleration with TF32 support (RTX 30-series optimization)
- 30-second audio chunking with 5-second overlap
- Hallucination filter for common false positives
- Automatic audio resampling to 16kHz
- Health check endpoint at `/health`

#### 2. Mistral Realtime Proxy (WebSocket)

For realtime streaming transcription with Mistral Voxtral, you need a WebSocket proxy server.

**Note**: This proxy script is not included in this repository. You can create a simple proxy using Python:

```python
# mistral_proxy.py (example - create this file)
import asyncio
import websockets
import json
import os

MISTRAL_API_KEY = os.environ.get("MISTRAL_API_KEY")

async def handle_client(websocket, path):
    async with websockets.connect(
        "wss://api.mistral.ai/v1/realtime",
        extra_headers={"Authorization": f"Bearer {MISTRAL_API_KEY}"}
    ) as mistral_ws:
        # Bridge between client and Mistral API
        async def forward_to_mistral():
            async for message in websocket:
                await mistral_ws.send(message)
        
        async def forward_to_client():
            async for message in mistral_ws:
                await websocket.send(message)
        
        await asyncio.gather(forward_to_mistral(), forward_to_client())

async def main():
    async with websockets.serve(handle_client, "localhost", 8765):
        await asyncio.Future()

if __name__ == "__main__":
    asyncio.run(main())
```

**Run the Proxy**:

```bash
# Set your Mistral API key
set MISTRAL_API_KEY=your_api_key_here

# Start the proxy
python mistral_proxy.py
```

**Configure WhisperInk**:

1. Right-click the WhisperInk tray icon
2. Select "API Providers"
3. Edit the Mistral provider:
   - **Base URL**: `http://127.0.0.1:8765`
   - **API Key**: Your Mistral API key (or leave blank if set in environment)

4. Switch to "Realtime" mode in the context menu

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

| Hotkey | Action |
|---------|--------|
| **Ctrl+Space** | Hold to record, release to transcribe. Mode determines behavior:
  - **Realtime**: WebSocket streaming to Mistral Voxtral proxy → live character-by-character typing
  - **Batch**: Records WAV → HTTP POST to active provider → result pasted via clipboard |
| **Ctrl+Alt** | Hold to record for AI query mode. Grabs selected text as context, transcribes voice instruction, sends both to chat LLM, pastes response. |

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

### Per-Model Parameter Profiles (New)

WhisperInk now supports per-provider **transcription model profiles** so you can quickly switch between tuned parameter sets.

Each provider can define multiple profiles, each with:
- `DisplayName` and `ModelId`
- Typed parameters:
  - `SendLanguage` + `Language`
  - `Temperature`
  - `ContextBiasMode` (`inherit`, `none`, `whisper_prompt`, `cohere_terms`)
  - `Prompt`
  - `ContextBiasTerms`
- `Hints` text for usage guidance
- `RawOverrides` for advanced multipart key/value injection

Profiles are configurable in **API Provider Settings** and switchable from the tray context menu under the Provider submenu.

#### Advanced Raw Overrides

Use raw overrides to inject provider/model-specific fields not covered by typed controls.

- Override item fields:
  - `Key`
  - `Value`
  - `ValueTypeHint` (`string`, `number`, `bool`, `json`)
  - `Enabled`
- Merge behavior:
  - Typed profile fields are built first
  - Raw overrides are applied afterward
  - Raw overrides win on key collisions
  - Protected key `file` cannot be overridden

This makes WhisperInk a practical testbed for ASR parameter exploration across OpenAI, Mistral, Cohere, ElevenLabs, local servers, and ONNX-backed local inference.

### Config Schema Version

Config now includes `ConfigSchemaVersion` and stores profile definitions under each provider.

Backward compatibility behavior:
- Existing provider fields still load
- If profiles are missing, WhisperInk synthesizes defaults and migrates legacy settings
- Legacy `ContextBiasTerms` and provider-level temperature/context mode are preserved as fallback paths

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

## Troubleshooting

### Realtime Mode Not Working

1. Ensure the Mistral proxy is running on `http://127.0.0.1:8765`
2. Check that your API key is correctly configured
3. Verify streaming delay settings (try 480ms for balanced latency/accuracy)

### Local ONNX Slow Performance

1. Use INT8 models instead of INT4 for better accuracy
2. Ensure you have a CUDA-capable GPU
3. Check that cuDNN is properly installed

### API Key Issues

1. Verify your API key has the correct permissions
2. Check that the provider endpoint URL is correct
3. Review the debug log at `%APPDATA%\.WhisperInk\debug.log`

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
