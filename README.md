# WhisperInk

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Hold a global hotkey, speak, and the transcript is typed or pasted into whichever application has focus. Works with multiple cloud ASR backends (Mistral Voxtral, OpenAI Whisper, ElevenLabs Scribe, Cohere Transcribe) and optional local inference paths (ONNX, GGUF via llama.cpp, Qwen3-ASR).

- **Repository**: https://github.com/praxeo/whisperinc
- **Platform**: Windows 10/11, .NET 8.0
- **License**: see `LICENSE`

---

## Quick install

Three commands from a fresh clone on a Windows box with the .NET 8 SDK:

```powershell
git clone https://github.com/praxeo/whisperinc.git
cd whisperinc
.\scripts\install.ps1 -Desktop
```

That publishes a self-contained build to `_publish\` and creates Start Menu + Desktop shortcuts. Launch from the Start Menu. Right-click the tray icon for the support menu; hold `Ctrl+Space` in any window to dictate.

Uninstall with `.\scripts\uninstall.ps1`. It removes shortcuts and the auto-start entry but preserves `%APPDATA%\.WhisperInk\` (config, history, models).

## Table of Contents

1. [Features](#features)
2. [Setting up on a new computer](#setting-up-on-a-new-computer)
3. [Configuring a provider](#configuring-a-provider)
4. [Hotkeys & modes](#hotkeys--modes)
5. [Optional local backends](#optional-local-backends)
6. [Architecture](#architecture)
7. [Configuration & data locations](#configuration--data-locations)
8. [Build & publish](#build--publish)
9. [Getting help](#getting-help)
10. [Troubleshooting](#troubleshooting)
11. [Design docs](#design-docs)

---

## Features

- **Global hotkey dictation** — `Ctrl+Space` to record and transcribe into any foreground app.
- **AI edit mode** — `Ctrl+Alt` grabs the currently-selected text, captures a voice instruction, sends both to a chat LLM, and pastes the result.
- **Multi-provider** — cloud (Mistral, OpenAI, ElevenLabs, Cohere) and local (ONNX, GGUF llama.cpp subprocess/server, Qwen3-ASR HTTP). Per-provider auth header, endpoint, model, temperature, and context-bias configuration.
- **Two dictation modes** — `Batch` (record → POST → paste via clipboard) works with every provider; `Realtime` (WebSocket streaming → per-character `WM_CHAR`) is Mistral-only via a local proxy.
- **Context biasing** — per-provider vocabulary hints (`whisper_prompt` for OpenAI-compatible, `cohere_terms` for Cohere v2, `keyterms` for ElevenLabs Scribe v2).
- **Optional post-processing** — a second LLM correction pass tuned for medical/technical dictation.
- **History log** — every transcription recorded locally, viewable from the tray.

---

## Setting up on a new computer

This is the fast path if you just want to use cloud providers — no GPU, no Python, no model downloads.

### 1. Install prerequisites

- **.NET 8 Desktop Runtime (x64)** — https://dotnet.microsoft.com/download/dotnet/8.0
  Pick *"Desktop Runtime"*, not just the runtime, or the WPF app won't launch.
- **Git for Windows** (only if building from source) — https://git-scm.com/download/win
- **.NET 8 SDK** (only if building from source) — same download page as above.

Verify:

```powershell
dotnet --list-runtimes
# should include: Microsoft.WindowsDesktop.App 8.0.x
```

### 2. Get the app

**Option A — build from source (recommended while there are no published releases):**

```powershell
git clone https://github.com/praxeo/whisperinc.git
cd whisperinc
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

The binary lands in `publish\WhisperInk.exe`. Move that folder wherever you want the app to live (e.g. `C:\Tools\WhisperInk\`).

**Option B — self-contained build** (bundles the .NET runtime, ~80MB, skips prerequisite #1):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

### 3. First run

Double-click `WhisperInk.exe`. The main window opens and a keyboard hook is installed. Leave the window open (minimize it) — closing it exits the hook. Optional: add a shortcut to `WhisperInk.exe` in `shell:startup` to auto-launch at login.

### 4. Grant microphone access

Windows → Settings → Privacy & Security → Microphone → allow desktop apps. On first recording, Windows may also prompt for access.

That's the minimum. From here, configure at least one provider and you can dictate.

---

## Configuring a provider

Right-click the window (or use the menu) and open **Providers…**.

WhisperInk ships with several defaults populated. For the easiest path, pick one cloud provider and paste its API key:

| Provider | Where to get a key | Notes |
|----------|---------------------|-------|
| **Mistral** | https://console.mistral.ai | Supports both Realtime and Batch. Also useful for AI-edit mode and post-processing. |
| **OpenAI** | https://platform.openai.com/api-keys | Whisper-1. Rock-solid; supports `whisper_prompt` vocabulary biasing. |
| **ElevenLabs Scribe v2** | https://elevenlabs.io | Custom `xi-api-key` header; supports keyterms list (~20% cost surcharge). |
| **Cohere Transcribe** | https://dashboard.cohere.com | Cohere v2 endpoint with `context_bias_terms` JSON array; temp 0.1 default. |

Then:

1. Select the provider in the list → paste your API key → **Save**.
2. Set it as **Active**.
3. Set **Mode → Batch** (Realtime only works with Mistral + the local proxy; see below).
4. Pick your microphone from the device dropdown.
5. Hold **Ctrl+Space**, speak, release. Text should paste into the focused window.

The debug log at `%APPDATA%\.WhisperInk\debug.log` is your first stop if something isn't working.

---

## Hotkeys & modes

| Hotkey | Behaviour |
|--------|-----------|
| **Hold Ctrl+Space** | Record for as long as held; on release, transcribe and insert. In Batch mode the transcript is pasted via `Ctrl+V`; in Realtime mode each delta is typed live with `WM_CHAR`. |
| **Hold Ctrl+Alt** | Record a voice *instruction*. On release, WhisperInk grabs the currently-selected text from the foreground app, sends both the selection and the transcribed instruction to the active provider's chat model, and pastes the response. |

### Batch mode

- Records to `~/Documents/MyRecordings/temp_audio.wav`.
- POSTs a multipart form to the provider's transcription endpoint.
- Optionally runs the transcript through a post-processing LLM (`PostProcessBatch` toggle) to clean up garbled terms.
- Pastes via clipboard + simulated `Ctrl+V`, with a leading space prepended.

### Realtime mode

- Mistral-only. Requires a local WebSocket proxy at `ws://localhost:8765/v1/realtime`.
- Streams audio chunks up; each transcription delta is typed directly into the window that was in focus when you started recording, using `PostMessage(WM_CHAR)`.
- Tunable `TargetStreamingDelayMs` (240–2400ms) trades latency for accuracy.

---

## Optional local backends

Everything below is *optional*. If you're happy with a cloud provider, skip this section.

### Cohere Transcribe server (FastAPI + Transformers)

A local FastAPI server for running Cohere Transcribe on your own GPU.

Requirements:
- Python 3.10+
- NVIDIA GPU with recent CUDA drivers (RTX 30-series or better recommended)
- 16 GB+ system RAM

```powershell
cd server
python -m venv venv
venv\Scripts\activate
pip install transformers>=4.52.0 torch soundfile fastapi uvicorn
python cohere_server.py
# listens on http://127.0.0.1:8101
```

Configure WhisperInk → add a provider:
- **Base URL**: `http://127.0.0.1:8101`
- **Transcription Endpoint**: `http://127.0.0.1:8101/v1/audio/transcriptions`
- **API Key**: *(blank)*
- **Model**: `cohere-transcribe-03-2026`

### Cohere GGUF via CrispASR (llama.cpp fork)

CrispASR is a whisper.cpp/llama.cpp fork that can run the Cohere Transcribe GGUF locally. Four provider variants ship for different deployments — CPU subprocess, HTTP server (CPU), HTTP server (CUDA), HTTP server (CUDA Q8). The CPU HTTP server (`cohere-gguf-server`) is the most useful default on a machine without an NVIDIA GPU.

**Performance expectation:** on a typical laptop CPU (8 threads, no GPU), the Q5_0 build runs at roughly real-time — a few-second dictation burst transcribes in a couple of seconds. Cloud is still snappier for long clips, but for normal dictation bursts local CPU is perfectly usable; the tradeoff is latency vs. offline/privacy, not "fast vs. unusable."

#### Prerequisites

- **Visual Studio 2022** with the *"Desktop development with C++"* workload (or standalone Build Tools 2022).
- **CMake 3.14+** — https://cmake.org/download/
- **Git**
- ~2 GB disk for the build + model.

For CUDA variants, also: **CUDA Toolkit 12.x** and an NVIDIA GPU with recent drivers.

#### 1. Download the GGUF model

```powershell
cd path\to\whisperinc
.\scripts\download-cohere-gguf.ps1
```

This fetches `cohere-transcribe-q5_0.gguf` (~1.45 GB) from HuggingFace (`cstr/cohere-transcribe-03-2026-GGUF`) into `%APPDATA%\.WhisperInk\cohere-gguf\`. Q5_0 is the sweet spot; edit `$variant` in the script to use `q4_k` (smaller) or `q6_k`/`q8_0` (more accuracy).

#### 2. Build CrispASR

```powershell
.\scripts\build-crispasr.ps1
```

What this does:
- Clones `https://github.com/CrispStrobe/CrispASR` as a **sibling of this repo** (e.g., if whisperinc is at `Documents\GitHub\whisperinc\`, CrispASR lands at `Documents\GitHub\CrispASR\`). The script derives paths from `$PSCommandPath`, no hardcoded user paths.
- Runs `cmake -B build -G "Visual Studio 17 2022" -A x64 -DGGML_CUDA=OFF -DWHISPER_BUILD_TESTS=OFF` then builds the `whisper-cli` target in Release. That target produces `crispasr.exe` via CMake's `OUTPUT_NAME` — the historical `whisper-cli` name is kept only for internal linking rules.
- Copies `crispasr.exe` and **every** `*.dll` from `build\bin\Release\` into `%APPDATA%\.WhisperInk\cohere-gguf\` — the 13-ish DLLs include `parakeet.dll`, `cohere.dll`, `crispasr.dll`, the `ggml*.dll` family, `whisper.dll`, etc. Copying only `ggml*` is not enough; the binary dynamically loads the per-backend DLLs at startup and silently exits with `STATUS_DLL_NOT_FOUND` if any are missing.

For a CUDA build on a GPU box, flip `-DGGML_CUDA=OFF` to `ON` in the script. Requires CUDA Toolkit 12.x on PATH.

The `-DWHISPER_BUILD_TESTS=OFF` flag skips a Catch2 `FetchContent` step that tries to clone from GitHub during configure — saves a few hundred MB and avoids failures on firewalled networks.

#### 3. Point WhisperInk at it

After step 2, `%APPDATA%\.WhisperInk\cohere-gguf\` should contain:

```
crispasr.exe
cohere-transcribe-q5_0.gguf   (or whichever GGUF you downloaded)
canary.dll, canary_ctc.dll, cohere.dll, crispasr.dll,
ggml.dll, ggml-base.dll, ggml-cpu.dll, granite_speech.dll,
parakeet.dll, qwen3_asr.dll, voxtral.dll, voxtral4b.dll, whisper.dll
```

In the WhisperInk UI, pick one of:
- **`Cohere Local (CrispASR GGUF)`** — uses `CohereGgufTranscriber`, one-shot subprocess per recording (simplest, highest per-call latency).
- **`Cohere Local (CrispASR server)`** — uses `CohereGgufServerTranscriber`, lazy-starts `crispasr.exe --server --host 127.0.0.1 --port 8766 -m <model> --backend cohere -l en -t 8 -np` on first use and keeps it alive. Recommended for the CPU path — cuts latency by keeping the model loaded.
- **`Cohere Local (CrispASR CUDA)`** / **`Cohere Local (CrispASR CUDA Q8)`** — same idea but targeting a CUDA-built binary and, for Q8, a different model file.

Set it Active, mode = Batch, then `Ctrl+Space` to test. First call takes a few extra seconds while the server boots and loads the model; subsequent calls are just inference.

Source files worth reading if you want to customize ports, flags, or the model filename: `CohereGgufTranscriber.cs`, `CohereGgufServerTranscriber.cs`, `CohereGgufCudaServerTranscriber.cs`, `CohereGgufCudaQ8ServerTranscriber.cs`.

### Parakeet Local (CrispASR) — auto-managed server

WhisperInk ships with a built-in `Parakeet Local (CrispASR)` provider that auto-spawns a `crispasr.exe --server` subprocess the first time you dictate with it selected, keeps the model resident between calls, and tears it down when WhisperInk exits. You don't need to keep a terminal open.

Parakeet TDT 0.6B at Q4_K is ~467 MB and noticeably faster than the 2B Cohere Transcribe model on CPU, so it's a good default for laptops without a GPU. The server exposes CrispASR's OpenAI-compatible `/v1/audio/transcriptions` endpoint, so WhisperInk uses its regular HTTP batch path — no realtime streaming, no special protocol.

**Performance you should expect:**
- CPU (modern laptop, 8 threads): RTFx 2–3× — a 2-second burst transcribes in <1s, a 10-second utterance in ~3–4s. Cold start adds ~2–3s model-load tax, once per session.
- CUDA (RTX 3090-class): RTFx 20–50× — near-instant for any reasonable dictation length. Requires a CUDA-built `crispasr.exe` and a CUDA-variant Parakeet provider (not wired up by default — see "Canary / Qwen3-ASR / Voxtral" below for the cloning pattern).
- Low-power ultrabook CPU (e.g., Ryzen U-series, Intel T-suffix): RTFx 1.5–2×. Still usable for dictation bursts; longer utterances feel slow.

#### One-time setup

You only need two files in place: `crispasr.exe` and a Parakeet GGUF, both under `%APPDATA%\.WhisperInk\cohere-gguf\`.

1. **crispasr.exe** — build CrispASR's current `main` branch from https://github.com/CrispStrobe/CrispASR (it now ships the unified multi-backend binary). If you already followed the [Cohere GGUF section](#cohere-gguf-via-crispasr-llamacpp-fork) above, you've got this.
2. **Parakeet GGUF** — one PowerShell line:

   ```powershell
   $dir = "$env:APPDATA\.WhisperInk\cohere-gguf"
   New-Item -ItemType Directory -Force -Path $dir | Out-Null
   curl.exe -L --fail --progress-bar `
     -o "$dir\parakeet-tdt-0.6b-v3-q4_k.gguf" `
     "https://huggingface.co/cstr/parakeet-tdt-0.6b-v3-GGUF/resolve/main/parakeet-tdt-0.6b-v3-q4_k.gguf"
   ```

   Any `parakeet-*.gguf` filename in that folder will be picked up; pick a different quant (`q8_0`, `q5_0`, `q4_k`, `f16`) by changing the URL filename.

#### Use it

In WhisperInk → **Providers…** → pick **`Parakeet Local (CrispASR)`** → set it Active. Mode = Batch. Hold `Ctrl+Space` to dictate. First call takes a few extra seconds while the server boots and loads the model; subsequent calls are just inference.

Changing the port in the provider's `Base URL` field (default `http://localhost:8103`) is honored — the spawned server binds to whatever port the UI says.

### Cohere Local Q4 (CrispASR) — auto-managed server

The second built-in auto-spawn provider. Same pattern as Parakeet — lazy-starts `crispasr.exe --server` on port 8104 when selected, keeps the model resident, tears it down on app exit — but runs the Cohere Transcribe 2B GGUF at Q4_K quantization (~1.4 GB). Trades off size/latency for Cohere's higher English accuracy ceiling.

**Performance you should expect:**
- CPU (modern laptop, 8 threads): RTFx 1–1.5× — roughly half Parakeet's speed at the same quant level, since the underlying model is ~3× larger. A 2-second burst transcribes in ~1–2s, a 10-second utterance in ~8–10s. Cold start adds ~3–5s.
- CUDA (RTX 3090-class): RTFx 15–30× — comfortably fast for any dictation length, once wired into a CUDA-built `crispasr.exe`.

Unlike Parakeet, Cohere GGUFs don't expose backend metadata that CrispASR's auto-detect reads, so WhisperInk's spawn call passes `--backend cohere` explicitly. That's handled in `MainWindow.xaml.cs` via the `backendHint` parameter on `CrispAsrServerTranscriber`.

#### One-time setup

Same `crispasr.exe` as the Parakeet path. The only extra file is the Q4_K GGUF:

```powershell
.\scripts\download-cohere-q4.ps1
```

Or inline:

```powershell
$dir = "$env:APPDATA\.WhisperInk\cohere-gguf"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
curl.exe -L --fail --progress-bar `
  -o "$dir\cohere-transcribe-q4_k.gguf" `
  "https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/main/cohere-transcribe-q4_k.gguf"
```

The file is literally named `cohere-transcribe-q4_k.gguf` — the dispatch in WhisperInk looks for that exact filename, not a glob, so don't rename it.

#### Use it

In WhisperInk → **Providers…** → pick **`Cohere Local Q4 (CrispASR)`** → Active, Batch. Ctrl+Space to dictate. Port defaults to 8104; change in the provider's Base URL field if another app is on that port.

### Cohere Local Q6_K (CrispASR) — auto-managed server

Same pattern as Q4 but swaps in `cohere-transcribe-q6_k.gguf` on port 8105. Q6_K is K-quant mixed-precision, so accuracy sits very close to F16 while the CPU RTFx stays effectively identical to Q4_K (~1.05–1.08× on 8 threads per the upstream benchmarks). Use this as the accuracy-first local Cohere; use Q4 only when disk footprint or memory pressure actually matters. The dispatch passes the same `backendHint: "cohere"` as the Q4 path.

#### One-time setup

```powershell
.\scripts\download-cohere-q6k.ps1
```

Or inline:

```powershell
$dir = "$env:APPDATA\.WhisperInk\cohere-gguf"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
curl.exe -L --fail --progress-bar `
  -o "$dir\cohere-transcribe-q6_k.gguf" `
  "https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/main/cohere-transcribe-q6_k.gguf"
```

Filename is fixed at `cohere-transcribe-q6_k.gguf` — the dispatch looks for that exact name, not a glob.

#### Use it

In WhisperInk → **Providers…** → pick **`Cohere Local Q6_K (CrispASR)`** → Active, Batch. Port defaults to 8105.

### What about Canary / Qwen3-ASR / Voxtral?

The built-in Parakeet preset is the only one with auto-spawn wired up right now. For any other CrispASR backend, the manual path still works: start the server yourself and point a cloned provider at it. Swap the GGUF file, adjust the port, done.

| Model | HuggingFace repo | Good for |
|-------|------------------|----------|
| `parakeet-tdt-0.6b-v3-q4_k.gguf` | `cstr/parakeet-tdt-0.6b-v3-GGUF` | Multilingual (25 EU), fast, word timestamps |
| `canary-1b-v2-q5_0.gguf` | `cstr/canary-1b-v2-GGUF` | Explicit-language control + speech translation |
| `qwen3-asr-0.6b-q4_k.gguf` | `cstr/qwen3-asr-0.6b-GGUF` | 30 languages + Chinese dialects |
| `voxtral-mini-3b-2507-q4_k.gguf` | `cstr/voxtral-mini-3b-2507-GGUF` | Speech-LLM, audio Q&A |
| `cohere-transcribe-q5_0.gguf` | `cstr/cohere-transcribe-03-2026-GGUF` | Highest English WER |

Manual start (leave running):

```powershell
crispasr.exe --server `
  -m "$env:APPDATA\.WhisperInk\cohere-gguf\canary-1b-v2-q5_0.gguf" `
  --host 127.0.0.1 --port 8104
```

Then in Providers… duplicate the Parakeet preset, change the name, point Base URL + Transcription Endpoint at `http://localhost:8104`. If there's interest we can generalize auto-spawn to any CrispASR model; for now, Parakeet is the turnkey one.

### Cohere ONNX (CPU, no server)

Runs Cohere Transcribe directly inside the WhisperInk process via ONNX Runtime — no subprocess, no HTTP. Slower than GPU paths for autoregressive decoding, but completely offline.

Place these in `%APPDATA%\.WhisperInk\cohere-onnx\`:
- `cohere-encoder.int4.onnx`
- `cohere-decoder.int4.onnx`
- `tokens.txt`

INT4 weights: `cstr/cohere-transcribe-onnx-int4` on HuggingFace. Swap filenames for INT8 if you have those.

### Qwen3-ASR local server

Expects an OpenAI-compatible transcription endpoint at `http://localhost:8102`. Any inference server that exposes `/v1/audio/transcriptions` in that shape will work.

### Mistral realtime proxy

Realtime mode requires a local proxy that bridges WhisperInk's WebSocket client to `wss://api.mistral.ai/v1/realtime`, injecting your API key as a header. A minimal Python example:

```python
# mistral_proxy.py
import asyncio, os, websockets

KEY = os.environ["MISTRAL_API_KEY"]

async def handle(ws, _path):
    async with websockets.connect(
        "wss://api.mistral.ai/v1/realtime",
        extra_headers={"Authorization": f"Bearer {KEY}"},
    ) as upstream:
        async def c2s():
            async for m in ws: await upstream.send(m)
        async def s2c():
            async for m in upstream: await ws.send(m)
        await asyncio.gather(c2s(), s2c())

async def main():
    async with websockets.serve(handle, "localhost", 8765):
        await asyncio.Future()

asyncio.run(main())
```

```powershell
set MISTRAL_API_KEY=sk-...
python mistral_proxy.py
```

Then set WhisperInk's proxy path in the config (or leave it to the default `ws://localhost:8765/v1/realtime`) and switch the dictation mode to **Realtime**.

---

## Architecture

### Core files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Global low-level keyboard hook, recording state machine, all transcription/AI logic, tray & context menu UI, clipboard/paste plumbing, WebSocket client. ~1800 LOC — the heart of the app. |
| `AppConfig.cs` | `ApiProvider` model (one per backend) and `AppConfig` (top-level settings, provider list, bias terms, post-process config). `CreateDefaults()` seeds the provider list. |
| `CohereOnnxTranscriber.cs` | In-process ONNX inference for Cohere Transcribe (encoder-decoder, 30s chunks, 5s overlap, greedy decoding). |
| `CohereGguf*Transcriber.cs` | Four variants for llama.cpp-based Cohere deployments (subprocess / HTTP CPU / HTTP CUDA / HTTP CUDA Q8). |
| `CrispAsrServerTranscriber.cs` | Generic adapter for the `crispasr.exe --server` mode — model-agnostic, auto-detects backend from GGUF metadata. Used by the Parakeet provider; the path new models should adopt. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for editing providers — URLs, keys, auth header, model field, bias mode, Scribe v2 keyterms. |
| `ContextBiasWindow.xaml(.cs)` | Global context-bias term list (used with `whisper_prompt` / `cohere_terms` modes). |
| `PromptWindow.xaml(.cs)` | System-prompt editor for AI-edit mode. |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | Local transcript log + viewer. |

### Provider system

`ApiProvider` captures every knob a transcription backend might need:

- `BaseUrl` + optional `TranscriptionEndpoint` override (full URL).
- `AuthHeaderName` — blank means `Authorization: Bearer <key>`; set to `xi-api-key` for ElevenLabs; etc.
- `ModelFieldName` — `"model"` or `"model_id"` (ElevenLabs).
- `TranscriptionModel`, `ChatModel`, `PostProcessModel`.
- `SupportsRealtime`, `SupportsTranscription`.
- `TranscriptionTemperature` — nullable; sent as multipart form field when set.
- `ContextBiasMode`:
  - `"none"` — no bias field.
  - `"whisper_prompt"` — comma-joined string in `prompt` (OpenAI, Groq, DeepInfra, local Whisper servers).
  - `"cohere_terms"` — JSON array in `context_bias_terms` (Cohere v2 cloud).
- `ScribeKeytermsRaw` — newline-delimited per-provider keyterms; sent as repeated `keyterms` multipart fields when the provider uses `xi-api-key` auth (ElevenLabs Scribe v2). Validated on send: ≤1000 terms, <50 chars, ≤5 words each.

Default providers (see `AppConfig.cs`):

| Id | Name | Transport | Notes |
|----|------|-----------|-------|
| `mistral` | Mistral | HTTPS + WS | Voxtral, realtime + batch |
| `openai` | OpenAI | HTTPS | Whisper-1, whisper_prompt bias |
| `elevenlabs` | ElevenLabs Scribe | HTTPS | `xi-api-key`, `model_id`, keyterms |
| `cohere-api` | Cohere Transcribe API | HTTPS | Cohere v2, temp 0.1, cohere_terms |
| `local` | Local Server | HTTP | `localhost:8100`, whisper_prompt |
| `cohere-onnx` | Cohere Local (ONNX) | in-process | Bypasses HTTP entirely |
| `cohere-gguf` | Cohere Local (CrispASR GGUF) | subprocess | llama.cpp CLI |
| `cohere-gguf-server` | Cohere Local (CrispASR server) | HTTP | llama.cpp server, CPU |
| `cohere-gguf-cuda-server` | Cohere Local (CrispASR CUDA) | HTTP | llama.cpp server, CUDA |
| `cohere-gguf-cuda-server-q8` | Cohere Local (CrispASR CUDA Q8) | HTTP | llama.cpp server, CUDA Q8, cohere_terms |
| `qwen3-asr` | Qwen3-ASR Local | HTTP | `localhost:8102`, OpenAI-compat |
| `parakeet-local` | Parakeet Local (CrispASR) | HTTP | `localhost:8103`, auto-spawned via `CrispAsrServerTranscriber` |
| `cohere-local-q4` | Cohere Local Q4 (CrispASR) | HTTP | `localhost:8104`, auto-spawned Q4_K Cohere Transcribe |
| `cohere-local-q6k` | Cohere Local Q6_K (CrispASR) | HTTP | `localhost:8105`, auto-spawned Q6_K Cohere Transcribe (accuracy-first) |

### Text injection

- **Realtime** — `PostMessage(WM_CHAR, ch, 0)` per character to the window handle captured at recording start. This bypasses IME and most focus-stealing issues.
- **Batch / AI** — `Clipboard.SetText` followed by a synthetic `Ctrl+V`. A single leading space is prepended so the result doesn't fuse to an adjacent word.

### Keyboard hook internals

- `SetWindowsHookEx(WH_KEYBOARD_LL, …)` installed on the UI thread.
- Synthetic key presses (the `Ctrl+V` paste in particular) are tagged with a sentinel flag (`0x5AFE`) in the hook's extra-info field so the hook ignores its own injections and avoids re-entry.
- `ReleaseAllModifierKeys()` runs after every recording to clear any physical modifier that was still held when the hook fired — prevents "stuck Ctrl" after long sessions.

### Cohere v2 multipart quirk

The Cohere v2 transcription endpoint rejects requests where the `file` part appears before string fields. WhisperInk therefore always appends string fields (`model`, `language`, `temperature`, `context_bias_terms`, `keyterms`) *before* the `file` part in the multipart body.

### WebSocket serialization

All sends on the Realtime WebSocket go through a `SemaphoreSlim(1,1)` so concurrent audio chunks + control frames can't interleave on the wire.

### UI sounds

Start/stop chirps are procedurally generated sine waves in memory — no asset files, nothing to ship.

---

## Configuration & data locations

| Path | Contents |
|------|----------|
| `%APPDATA%\.WhisperInk\config.json` | All providers, active id, mode, mic selection, system prompt, bias terms, post-process prompt & toggle, proxy path, streaming delay. |
| `%APPDATA%\.WhisperInk\debug.log` | Rolling log. First place to check for any failure. |
| `%APPDATA%\.WhisperInk\history.json` | Transcription history (viewable from the tray). |
| `%APPDATA%\.WhisperInk\cohere-onnx\` | ONNX weights + `tokens.txt` (only if you use the ONNX provider). |
| `~/Documents/MyRecordings/temp_audio.wav` | The most recent Batch-mode recording (overwritten each time). |

Config is loaded on startup and rewritten after any settings change. Safe to back up or sync.

---

## Build & publish

```powershell
# Framework-dependent (smaller; requires the .NET 8 Desktop Runtime on the target machine)
dotnet publish -c Release -r win-x64 --self-contained false

# Self-contained (bundles the runtime; ~80 MB, no prerequisites)
dotnet publish -c Release -r win-x64 --self-contained true
```

Helper scripts:

- `publish.ps1` — self-contained build into `_publish\`.
- `publish-framework-dependent.ps1` — smaller framework-dependent build into `_publish-fd\`.
- `scripts\install.ps1 [-Desktop]` — runs the self-contained publish then creates Start Menu (and optionally Desktop) shortcuts. The one-shot path from a fresh clone.
- `scripts\install-shortcuts.ps1 [-Desktop]` — create shortcuts for an already-published build.
- `scripts\uninstall.ps1 [-RemoveBinaries]` — remove shortcuts and the auto-start registry entry. Leaves `%APPDATA%\.WhisperInk\` alone unless you also pass `-RemoveBinaries`, which wipes `_publish*` too.
- `scripts\generate-icon.ps1` — regenerate `Assets\icon.ico` from code (only needed if you want to change the glyph).

NuGet dependencies (`WhisperInk.csproj`):
- `NAudio 2.2.1` — microphone capture.
- `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.24.4` — ONNX inference; loads DirectML, falls back to CPU.

---

## Getting help

If something isn't working, right-click the tray icon and pick **Copy support bundle**. That drops a zip onto your desktop and puts it on the clipboard so you can paste it straight into Slack / Discord / an issue. The bundle contains:

- The last 500 lines of `%APPDATA%\.WhisperInk\debug.log`
- `config.json` with `ApiKey` fields redacted (`***redacted***`)
- `about.txt` with app version, commit hash, .NET and OS versions, installed providers, and which local model files are present

API keys are redacted; GGUF weights are never included (too large).

For a quick live view of what the active provider needs, the tray also has **Diagnose active provider** — it prints a file/port check block (`crispasr.exe FOUND 1.1 MB`, `port 8104 reachable: YES`) so you can see exactly what's in place before you touch anything.

**Tray menu quick reference:**

- **Show Window** — restore the floating bar (left-click or double-click the tray icon does the same thing).
- **Open debug log / config folder / model folder** — opens the paths in Notepad / Explorer.
- **Copy support bundle** — described above.
- **Diagnose active provider** — on-demand health probe with per-file detail.
- **About…** — version, commit hash, build date.
- **View README** — opens this page.
- **Quit on close** — if checked, the X / Alt+F4 actually exits instead of hiding.
- **Launch at Windows start** — HKCU Run entry; user-level, no admin needed.
- **Quit** — explicit exit.

---

## Troubleshooting

**App launches but Ctrl+Space does nothing**
The keyboard hook needs the main window alive. Don't close it — minimize it. Also check `debug.log` for hook-install failures (rare; usually means another process is already holding a global hook).

**"The .NET runtime is not installed"**
Install the **.NET 8 Desktop Runtime (x64)**, not the base runtime. Alternatively, rebuild with `--self-contained true`.

**Recording starts but nothing pastes**
Check `debug.log` for the HTTP response. 401/403 = bad API key. 404 = wrong endpoint (especially common if you customized the TranscriptionEndpoint field). 422 on ElevenLabs with keyterms = swap the repeated form fields for a JSON fallback (commented in `MainWindow.xaml.cs` at the keyterms block).

**Realtime mode does nothing**
Mistral only. The local proxy must be running on `127.0.0.1:8765`, your API key must be in the proxy's environment, and mode must be set to `Realtime`. Try `TargetStreamingDelayMs = 480` for a sensible default.

**Local ONNX is slow**
Autoregressive decoding on CPU/DirectML is genuinely slow — a 10s utterance can take several seconds. Prefer a cloud provider or a GGUF CUDA server if you have an NVIDIA GPU. INT8 is more accurate but slower than INT4.

**Parakeet/Cohere Q4 is slower than expected (RTFx <2× on a laptop)**
Windows' default **Balanced** power plan throttles CPU to its base clock even while plugged in — on a Ryzen 5825U that's 2.0 GHz versus the 4.5 GHz boost. Roughly halves ASR throughput. Switch to Ultimate Performance:

```powershell
powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61
powercfg /setactive 5898ace7-acb8-479d-b9c1-54af0f151d1b
```

First command creates the hidden Ultimate Performance scheme from its well-known GUID, second activates it. No reboot needed. Verify with `Get-CimInstance Win32_Processor` under load — `CurrentClockSpeed` should now reach `MaxClockSpeed` under load, not sit at base.

**CrispASR/Parakeet returns empty transcripts (or `crispasr.exe --help` prints nothing)**
`crispasr.exe` is exiting with `STATUS_DLL_NOT_FOUND` (exit code `-1073741515` / `0xC0000135`) before it can print anything. One or more of the per-backend DLLs is missing from `%APPDATA%\.WhisperInk\cohere-gguf\`. Re-run the deploy step and make sure **every** `*.dll` from `build\bin\Release\` gets copied, not just `ggml*`. The full set on a current build is 13 files: `canary`, `canary_ctc`, `cohere`, `crispasr`, `ggml`, `ggml-base`, `ggml-cpu`, `granite_speech`, `parakeet`, `qwen3_asr`, `voxtral`, `voxtral4b`, `whisper`. Verify with:

```powershell
$dir = "$env:APPDATA\.WhisperInk\cohere-gguf"
& "$dir\crispasr.exe" --help 2>&1 | Select-Object -First 5
# Exit code 0 and help text = healthy. Exit code -1073741515 = missing DLL.
```

**CrispASR build succeeds but no executable appears**
Check `C:\path\to\CrispASR\build\bin\Release\` — the binary is `crispasr.exe`, not `whisper-cli.exe` (CMake renames via `OUTPUT_NAME`). On Windows there's no `whisper-cli` symlink; that's a Unix-only step in the upstream CMakeLists.

**Stuck modifier key after a recording**
Should auto-clear via `ReleaseAllModifierKeys()`. If it ever happens, tapping the key once releases it. Capture a `debug.log` excerpt and file an issue.

**Mic not listed**
NAudio enumerates WASAPI devices on launch. Plug in your mic *before* starting WhisperInk, or restart the app after plugging in.

---

## Design docs

Longer-form plans and sprint prompts live under `plans/`:

- [`plans/packaging-polish-prompt.md`](plans/packaging-polish-prompt.md) — the specification that drove the tray icon, install scripts, health probe, support bundle, diagnose flow, and auto-start wiring.

---

## Contributing

PRs welcome. `CLAUDE.md` has the quick architecture summary that Claude Code uses when working in this repo — a good orientation for new contributors too.
