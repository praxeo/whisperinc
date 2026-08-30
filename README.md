# WhisperInk

A WPF (C#/.NET 8) system-wide dictation tool for Windows. Hold a global hotkey, speak, and the transcript is typed or pasted into whichever application has focus. Works with multiple cloud ASR backends (Mistral Voxtral, OpenAI Whisper, ElevenLabs Scribe, Cohere Transcribe, Deepgram Nova-3, Google Chirp 3, Soniox, Modulate Velma 2, Smallest.ai Waves, Reson8) and optional local inference paths (GGUF via CrispASR/llama.cpp, incl. Qwen3-ASR 1.7B).

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
- **Multi-provider** — cloud (Mistral, OpenAI, ElevenLabs, Cohere, Deepgram, Google Chirp 3, Soniox, Modulate, Smallest.ai, Reson8) and local (GGUF via CrispASR subprocess/server, incl. Qwen3-ASR 1.7B). Per-provider auth header, endpoint, model, temperature, and context-bias configuration.
- **Batch dictation** — record → POST to the active provider → paste via clipboard; works with every provider.
- **Context biasing** — one shared term list, routed to each provider's native mechanism automatically (prompt glossary for OpenAI, `context_bias` for Mistral, `keyterms` for ElevenLabs, `hotwords` for local CrispASR/Parakeet, phrase sets for Google, context terms for Soniox, `custom_terms` for Modulate, `phrases` for Reson8). Providers with no biasing surface at all — Cohere Transcribe, Smallest.ai — log the ignored terms rather than dropping them silently.
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
| **Mistral** | https://console.mistral.ai | Voxtral transcription. |
| **OpenAI** | https://platform.openai.com/api-keys | Whisper-1. Rock-solid; supports `whisper_prompt` vocabulary biasing. |
| **ElevenLabs Scribe v2** | https://elevenlabs.io | Custom `xi-api-key` header; supports keyterms list (~20% cost surcharge). |
| **Cohere Transcribe** | https://dashboard.cohere.com | Cohere v2 endpoint; temp 0.1 default. No native vocabulary biasing. |
| **Modulate Velma 2** | https://platform.modulate.ai | Three presets, one per batch model. Pick **Multilingual** for `custom_terms` vocabulary biasing, or **English Fast** for the lowest latency (no biasing). |
| **Smallest.ai Waves** | https://smallest.ai | Two presets. **Pulse Pro** is English-only and the better dictation model; **Pulse** covers 46 languages. Neither supports vocabulary biasing of any kind, so the shared Context Bias list is inert here. Note the API retains request content by default. |
| **Reson8** | https://console.reson8.dev | One preset (the API has no `model` parameter). Real vocabulary biasing via `phrases` (≤250 terms), plus `custom_model_id` for a persistent 50,000-phrase vocabulary. **Ten languages only** (`de/en/es/fr/fy/it/nl/pl/pt/sv`) — picking any other from the Language dropdown falls back to auto-detect rather than failing. Wired but not yet live-tested. |

Then:

1. Select the provider in the list → paste your API key → **Save**.
2. Set it as **Active**.
3. Pick your microphone from the device dropdown.
4. Hold **Ctrl+Space**, speak, release. Text should paste into the focused window.

The debug log at `%APPDATA%\.WhisperInk\debug.log` is your first stop if something isn't working.

---

## Hotkeys & modes

| Hotkey | Behaviour |
|--------|-----------|
| **Hold Ctrl+Space** | Record for as long as held; on release, transcribe and insert. The transcript is pasted via `Ctrl+V`. |

### Batch mode

- Records to `~/Documents/MyRecordings/temp_audio.wav`.
- POSTs a multipart form to the provider's transcription endpoint.
- Pastes via clipboard + simulated `Ctrl+V`, with a leading space prepended.

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
crispasr.dll, whisper.dll,
ggml.dll, ggml-base.dll, ggml-cpu.dll, ggml-cuda.dll,
cublas64_12.dll, cublasLt64_12.dll, cudart64_12.dll   (CUDA asset only)
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

### Cohere Local Q4 (CrispASR) — auto-managed server  *(retired)*

> **Retired 2026-06-12.** Removed from the shipped defaults — `cohere-local-q6k` covers the GPU path at near-F16 accuracy, `cohere-gguf-server` stays the CPU fallback. The id still loads for configs that already carry it and port 8104 stays reserved, but a fresh install will not show it. Kept here because the auto-spawn pattern it describes is exactly how every local preset works.

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

### Adding your own local model

**Every local preset auto-spawns.** `CrispAsrServerTranscriber` is generic — it starts `crispasr.exe --server` on first use, keeps the model resident, and shuts it down when you switch away. Adding a model is **config only**: no rebuild, no C#, and nothing to start by hand.

Three steps.

**1. Check the backend is compiled in.** One command, and it settles whether the model is usable at all:

```powershell
& "$env:APPDATA\.WhisperInk\cohere-gguf\crispasr.exe" --list-backends
```

The `--backend` names are the left column. A name here works today; a name that appears only in CrispASR's online docs may not be in your binary yet — update it first (see [Updating CrispASR](#build--publish)).

**2. Download the GGUF** into `%APPDATA%\.WhisperInk\cohere-gguf\`:

| Model | HuggingFace repo | Good for |
|-------|------------------|----------|
| `parakeet-tdt-0.6b-v3-q4_k.gguf` | `cstr/parakeet-tdt-0.6b-v3-GGUF` | Multilingual (25 EU), fast, word timestamps |
| `parakeet-rnnt-1.1b-q4_k.gguf` | `cstr/parakeet-rnnt-1.1b-GGUF` | Stronger English than TDT; needs `LocalPuncModel` |
| `canary-1b-v2-q5_0.gguf` | `cstr/canary-1b-v2-GGUF` | Explicit-language control + speech translation |
| `qwen3-asr-1.7b-q4_k.gguf` | `cstr/qwen3-asr-1.7b-GGUF` | 30 languages + Chinese dialects; **shipped preset** (port 8112), biasing that works |
| `qwen3-asr-0.6b-q4_k.gguf` | `cstr/qwen3-asr-0.6b-GGUF` | Smaller sibling (~500 MB); needs its own preset + glob |
| `voxtral-mini-3b-2507-q4_k.gguf` | `cstr/voxtral-mini-3b-2507-GGUF` | Speech-LLM, audio Q&A |
| `cohere-transcribe-q6_k.gguf` | `cstr/cohere-transcribe-03-2026-GGUF` | Strong English, near-F16 at Q6_K |

**3. Add a provider entry** to `%APPDATA%\.WhisperInk\config.json` (with WhisperInk closed — it rewrites the file when you change settings), then restart. It shows up under 🔌 Provider.

```json
{
  "Id": "canary-local",
  "Name": "Canary Local (CrispASR)",
  "BaseUrl": "http://localhost:8113",
  "TranscriptionEndpoint": "http://localhost:8113/v1/audio/transcriptions",
  "TranscriberKind": "LocalCrispAsrServer",
  "LocalServerPort": 8113,
  "LocalModelGlob": "canary-*.gguf",
  "LocalBackendHint": "canary",
  "BiasMechanism": "none",
  "Language": "en"
}
```

**Pick a free port.** Each local preset spawns its own server, so ports can't be shared. Taken: **8103, 8105–8109, 8112, 8766**. Retired but still claimed by old configs: 8102, 8104, 8110, 8111, 8767, 8768. **Next free: 8113.**

**Make `LocalModelGlob` specific.** All presets share one folder and the first filename match wins, so a loose glob will quietly load a *different* model — which looks like bad accuracy, not a config error. `parakeet-*.gguf` matches both Parakeet models, hence the pinned `parakeet-tdt-*` and `parakeet-rnnt-1.1b-*`.

**Optional fields worth knowing:**

- `LocalBackendHint` — only if the GGUF doesn't auto-detect. Cohere needs `"cohere"`, Voxtral 3B `"voxtral"`, Voxtral 4B `"voxtral4b"`, Granite `"granite"`. Harmless to set anyway.
- `LocalPuncModel: "fullstop"` — restores punctuation on models that emit none (Parakeet RNNT/CTC). Skip it for speech-LLM models like Qwen3-ASR, which punctuate themselves.
- `LocalGpuBackend` — blank inherits the global setting; set `"cpu"` to pin one preset to CPU.
- `BiasMechanism: "hotwords"` — enables Context Bias terms. Genuinely effective on Qwen3-ASR and Voxtral 3B (the terms go into the model's prompt); weak on Parakeet; ignored by Cohere, Granite and Voxtral 4B.

**Test it before relying on it** — run the same command WhisperInk will, so a problem is clearly the model's and not your config:

```powershell
& "$env:APPDATA\.WhisperInk\cohere-gguf\crispasr.exe" --server --host 127.0.0.1 --port 8113 `
  -m "$env:APPDATA\.WhisperInk\cohere-gguf\canary-1b-v2-q5_0.gguf" -t 8 -np --backend canary
# then, in another shell:
curl.exe -s http://127.0.0.1:8113/health
curl.exe -s -F "file=@jfk.wav" http://127.0.0.1:8113/v1/audio/transcriptions
```

A cold CUDA load can take up to two minutes to answer `/health` — the model is uploaded to VRAM and warmed at startup. That's normal, and only happens once per server.

**Cloud providers** work the same way if the API speaks the OpenAI multipart shape: same JSON entry, but leave `TranscriberKind` as `"Http"` and set `BaseUrl`, `AuthHeaderName` (blank means `Bearer`), `ModelFieldName` (`model` or `model_id`) and `TranscriptionModel`. APIs with their own protocol — Deepgram, Modulate, Smallest.ai, Reson8, Soniox, Google Chirp 3 — need a small code change instead; see `CLAUDE.md` → **Adding a provider**.

### Qwen3-ASR 1.7B local (auto-spawned)

Ships as the `qwen3-asr-1.7b-local` preset — auto-spawned by WhisperInk like the Parakeet and Cohere presets, so there is nothing to start by hand. Drop the GGUF in `%APPDATA%\.WhisperInk\cohere-gguf\` and pick the provider:

```powershell
curl.exe -L -o "$env:APPDATA\.WhisperInk\cohere-gguf\qwen3-asr-1.7b-q4_k.gguf" `
  https://huggingface.co/cstr/qwen3-asr-1.7b-GGUF/resolve/main/qwen3-asr-1.7b-q4_k.gguf
```

~1.4 GB. WhisperInk then spawns `crispasr.exe --server --backend qwen3-1.7b -m <that file> --port 8112` on first use and keeps it resident.

Two things make this preset different from the other local ones:

- **Context-bias terms actually work.** Qwen3-ASR is a speech-LLM, so the bias list is spliced into its decoder prompt rather than into a CTC trie. On a clinical test set it recovered `hematochezia` (twice) and `ureterolithiasis` where the unbiased run produced "hematuria", "hematemesis" and "bursitis with edema" — with no damage to the control clips. Keep the list tight: a 40-term list still fixed 5 of 6 but softened `ureterolithiasis` into "ureteral lithiasis".
- **Punctuation and casing are native**, so unlike `parakeet-rnnt-local` it needs no `LocalPuncModel`.

This replaced the old `qwen3-asr` preset (a plain HTTP entry pointing at a server on `localhost:8102` that you had to run yourself). If you still want that arrangement, add a provider with `TranscriberKind: "Http"` and any inference server exposing `/v1/audio/transcriptions`.

---

## Architecture

### Core files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Global low-level keyboard hook, recording state machine, transcription logic, tray & context menu UI, clipboard/paste plumbing. ~1800 LOC — the heart of the app. |
| `AppConfig.cs` | `ApiProvider` model (one per backend) and `AppConfig` (top-level settings, provider list, bias terms). `CreateDefaults()` seeds the provider list. |
| `CrispAsrServerTranscriber.cs` | Generic adapter for `crispasr.exe --server` — one class for every GGUF backend (Cohere, Parakeet, Voxtral, Granite, …) via config-only provider entries. |
| `DeepgramTranscriber.cs` | Deepgram Listen API (`/v1/listen`) — raw-body POST, `Token` auth, query-param options, Nova-3 `keyterm` biasing. |
| `ModulateTranscriber.cs` | Modulate Velma 2 batch — multipart `upload_file`, `X-API-Key` auth, model selected by endpoint path, `custom_terms` biasing via the JSON `config` field. |
| `SmallestTranscriber.cs` | Smallest.ai Waves batch (`/waves/v1/stt/`) — raw-body POST, `Bearer` auth, query-param options, transcript at top-level `transcription`. No biasing surface; `language` is a strict enum with per-region entitlements, so it is always sent explicitly. |
| `Reson8Transcriber.cs` | Reson8 prerecorded (`/v1/speech-to-text/prerecorded`) — raw-body POST, `ApiKey` auth, query-param options, transcript at top-level `text`, RFC 7807 errors. Real `phrases` biasing. `language` is the mirror image of Smallest's: auto-detect means *omitting* the param, and the six codes in WhisperInk's dropdown that Reson8 doesn't support are dropped rather than sent (they would 400 every dictation). |
| `CrispAsrServerTranscriber.cs` | Generic adapter for the `crispasr.exe --server` mode — model-agnostic, auto-detects backend from GGUF metadata. Used by the Parakeet provider; the path new models should adopt. |
| `ProviderSettingsWindow.xaml(.cs)` | GUI for editing providers — URLs, keys, auth header, model field, a read-only biasing-mechanism line, and Parakeet hotword-boost / Scribe v2 extra keyterms. |
| `ContextBiasWindow.xaml(.cs)` | Global context-bias term list — the single source routed to each provider's native biasing field. |
| `HistoryService.cs`, `HistoryWindow.xaml(.cs)` | Local transcript log + viewer. |

### Provider system

`ApiProvider` captures every knob a transcription backend might need:

- `BaseUrl` + optional `TranscriptionEndpoint` override (full URL).
- `AuthHeaderName` — blank means `Authorization: Bearer <key>`; set to `xi-api-key` for ElevenLabs; etc.
- `ModelFieldName` — `"model"` or `"model_id"` (ElevenLabs).
- `TranscriptionModel`.
- `SupportsTranscription`.
- `TranscriptionTemperature` — nullable; sent as multipart form field when set.
- `BiasMechanism` (baked per provider; never user-set) routes the single shared bias list to that provider's native field:
  - `"whisper_prompt"` — labeled glossary in `prompt` (OpenAI, local prompt-aware servers).
  - `"mistral_context_bias"` — comma-joined string in `context_bias` (Mistral Voxtral batch, ≤100).
  - `"elevenlabs_keyterms"` — repeated `keyterms` fields (ElevenLabs Scribe v2), sourced from the shared list.
  - `"hotwords"` — comma-joined `hotwords` for local CrispASR servers (`HotwordsBoost` tunes the Parakeet trie — off by default, since boosting can garble neighboring words; no-op on Cohere/Granite/Voxtral-4B).
  - `"phrase_sets"` / `"context_terms"` / `"deepgram_keyterm"` / `"modulate_custom_terms"` / `"reson8_phrases"` — Google / Soniox / Deepgram / Modulate / Reson8, handled natively in their transcribers. Reson8 is the one where an over-long list *degrades* accuracy rather than being ignored — keep it tight.
  - `"none"` — provider has no biasing field (e.g. Cohere Transcribe v2, Smallest.ai Waves).
- `ScribeKeytermsRaw` — optional ElevenLabs-only extra keyterms, merged with the shared list and validated together (≤1000 terms, <50 chars, ≤5 words, illegal chars dropped).

Default providers (see `AppConfig.cs`):

| Id | Name | Transport | Notes |
|----|------|-----------|-------|
| `mistral` | Mistral | HTTPS | Voxtral batch |
| `openai` | OpenAI | HTTPS | Whisper-1, whisper_prompt bias |
| `elevenlabs` | ElevenLabs Scribe | HTTPS | `xi-api-key`, `model_id`, keyterms |
| `cohere-api` | Cohere Transcribe API | HTTPS | Cohere v2, temp 0.1; no native biasing |
| `local` | Local Server | HTTP | `localhost:8100`, whisper_prompt |
| `cohere-gguf` | Cohere Local (CrispASR GGUF) | subprocess | llama.cpp CLI |
| `cohere-gguf-server` | Cohere Local (CrispASR server) | HTTP | llama.cpp server, CPU |
| `cohere-gguf-cuda-server` | Cohere Local (CrispASR CUDA) | HTTP | llama.cpp server, CUDA |
| `cohere-gguf-cuda-server-q8` | Cohere Local (CrispASR CUDA Q8) | HTTP | llama.cpp server, CUDA Q8, cohere_terms |
| `qwen3-asr-1.7b-local` | Qwen3-ASR 1.7B Local (CrispASR) | HTTP | `localhost:8112`, auto-spawned `--backend qwen3-1.7b`; real prompt-splice biasing, native punctuation |
| `parakeet-local` | Parakeet Local (CrispASR) | HTTP | `localhost:8103`, auto-spawned via `CrispAsrServerTranscriber` |
| ~~`cohere-local-q4`~~ | Cohere Local Q4 (CrispASR) | HTTP | `localhost:8104` — **retired** from defaults 2026-06-12; still loads from existing configs |
| `cohere-local-q6k` | Cohere Local Q6_K (CrispASR) | HTTP | `localhost:8105`, auto-spawned Q6_K Cohere Transcribe (accuracy-first) |
| `modulate` | Modulate Velma 2 (Multilingual) | HTTPS | `X-API-Key`, `custom_terms` biasing |
| `modulate-english-fast` | Modulate Velma 2 English Fast | HTTPS | Lowest latency; English-only, no biasing |
| `modulate-multilingual-fast` | Modulate Velma 2 Multilingual Fast | HTTPS | Any language, no metadata, no biasing |
| `smallest-pulse-pro` | Smallest.ai Pulse Pro (English) | HTTPS | Bearer, raw-body POST; English-only, no biasing |
| `smallest-pulse` | Smallest.ai Pulse (Multilingual) | HTTPS | Same endpoint, `?model=pulse`; 46 languages, no biasing |
| `reson8` | Reson8 | HTTPS | `ApiKey` auth, raw-body POST; `phrases` biasing (≤250), `custom_model_id` for larger vocabularies; ten languages |

> This table lags `AppConfig.CreateDefaults()` — see the provider table in `CLAUDE.md` for the current list.

### Text injection

- **Batch** — `Clipboard.SetText` followed by a synthetic `Ctrl+V`. A single leading space is prepended so the result doesn't fuse to an adjacent word.

### Keyboard hook internals

- `SetWindowsHookEx(WH_KEYBOARD_LL, …)` installed on the UI thread.
- Synthetic key presses (the `Ctrl+V` paste in particular) are tagged with a sentinel flag (`0x5AFE`) in the hook's extra-info field so the hook ignores its own injections and avoids re-entry.
- `ReleaseAllModifierKeys()` runs after every recording to clear any physical modifier that was still held when the hook fired — prevents "stuck Ctrl" after long sessions.

### Cohere v2 multipart quirk

The Cohere v2 transcription endpoint rejects requests where the `file` part appears before string fields. WhisperInk therefore always appends string fields (`model`, `language`, `temperature`, and any bias field such as `context_bias` / `keyterms`) *before* the `file` part in the multipart body.

### UI sounds

Start/stop chirps are procedurally generated sine waves in memory — no asset files, nothing to ship.

---

## Configuration & data locations

| Path | Contents |
|------|----------|
| `%APPDATA%\.WhisperInk\config.json` | All providers, active id, mic selection, bias terms. |
| `%APPDATA%\.WhisperInk\debug.log` | Rolling log. First place to check for any failure. |
| `%APPDATA%\.WhisperInk\history.json` | Transcription history (viewable from the tray). |
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
- `Google.Apis.Auth 1.69.0` — Google Chirp 3 OAuth (service-account tokens).

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

**Parakeet/Cohere Q4 is slower than expected (RTFx <2× on a laptop)**
Windows' default **Balanced** power plan throttles CPU to its base clock even while plugged in — on a Ryzen 5825U that's 2.0 GHz versus the 4.5 GHz boost. Roughly halves ASR throughput. Switch to Ultimate Performance:

```powershell
powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61
powercfg /setactive 5898ace7-acb8-479d-b9c1-54af0f151d1b
```

First command creates the hidden Ultimate Performance scheme from its well-known GUID, second activates it. No reboot needed. Verify with `Get-CimInstance Win32_Processor` under load — `CurrentClockSpeed` should now reach `MaxClockSpeed` under load, not sit at base.

**CrispASR/Parakeet returns empty transcripts (or `crispasr.exe --help` prints nothing)**
`crispasr.exe` is exiting with `STATUS_DLL_NOT_FOUND` (exit code `-1073741515` / `0xC0000135`) before it can print anything. One or more DLLs is missing from `%APPDATA%\.WhisperInk\cohere-gguf\`. Re-run the deploy step and make sure **every** `*.dll` gets copied, not just `ggml*`. Upstream consolidated the per-backend DLLs into `crispasr.dll` between v0.7.1 and v0.8.30, so a current build needs only `crispasr`, `whisper`, `ggml`, `ggml-base`, `ggml-cpu`, `ggml-cuda`, plus `cublas64_12`, `cublasLt64_12` and `cudart64_12` on the CUDA asset. Older builds shipped 13 separate per-backend DLLs (`canary`, `canary_ctc`, `cohere`, `granite_speech`, `parakeet`, `qwen3_asr`, `voxtral`, `voxtral4b`, …) and need all of them present. Verify with:

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
