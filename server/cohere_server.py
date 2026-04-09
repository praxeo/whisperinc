# cohere_server.py
# FastAPI server for Cohere Transcribe — native transformers path
# Requires: transformers>=4.52.0, torch, soundfile, fastapi, uvicorn
#
# Usage:
#   python cohere_server.py
#   (runs on http://127.0.0.1:8101)

import io
import time
import numpy as np
import soundfile as sf
import torch
from contextlib import asynccontextmanager
from fastapi import FastAPI, UploadFile, File, Form

# ── Perf: TF32 matrix math (free speedup on RTX 30-series) ──
torch.backends.cuda.matmul.allow_tf32 = True
torch.backends.cudnn.allow_tf32 = True

# ── Config ──
MODEL_ID = "CohereLabs/cohere-transcribe-03-2026"
DEVICE = "cuda:0"
MAX_NEW_TOKENS = 512
CHUNK_SECONDS = 30
OVERLAP_SECONDS = 5
PORT = 8101

# ── Hallucination reduction ──
MIN_AUDIO_SECONDS = 0.5

HALLUCINATION_PHRASES = {
    "thank you", "thank you.", "thanks", "thanks.",
    "thanks for watching", "thanks for watching.",
    "thank you for watching", "thank you for watching.",
    "please subscribe", "subscribe",
    "you", "you.", ".", "...",
}

def is_hallucination(text: str) -> bool:
    return text.strip().lower() in HALLUCINATION_PHRASES

# ── Globals ──
model = None
processor = None

def load_model():
    global model, processor
    from transformers import AutoProcessor, CohereAsrForConditionalGeneration

    print("Loading processor...")
    processor = AutoProcessor.from_pretrained(MODEL_ID)

    print(f"Loading model on {DEVICE} (bfloat16)...")
    model = CohereAsrForConditionalGeneration.from_pretrained(
        MODEL_ID,
        device_map=DEVICE,
        torch_dtype=torch.bfloat16,
        attn_implementation="sdpa",
    )
    model.eval()
    print(f"Model loaded. dtype={model.dtype}, device={model.device}")

    print("Warming up model...")
    warmup_silence = np.zeros(16000, dtype=np.float32)
    transcribe_chunk(warmup_silence, 16000, language="en")
    print("Warm-up complete. Server ready.")

def transcribe_audio(audio_array: np.ndarray, sr: int, language: str = "en") -> str:
    if sr != 16000:
        import librosa
        audio_array = librosa.resample(audio_array, orig_sr=sr, target_sr=16000)
        sr = 16000

    chunk_samples = CHUNK_SECONDS * sr
    stride_samples = chunk_samples - OVERLAP_SECONDS * sr

    if len(audio_array) <= chunk_samples:
        chunks = [audio_array]
    else:
        chunks = []
        offset = 0
        while offset < len(audio_array):
            end = min(offset + chunk_samples, len(audio_array))
            chunks.append(audio_array[offset:end])
            offset += stride_samples

    results = []
    for chunk in chunks:
        text = transcribe_chunk(chunk, sr, language)
        if text.strip() and not is_hallucination(text):
            results.append(text.strip())
    return " ".join(results)

def transcribe_chunk(audio_chunk: np.ndarray, sr: int, language: str = "en") -> str:
    t0 = time.perf_counter()

    inputs = processor(
        audio_chunk.astype(np.float32),
        sampling_rate=sr,
        return_tensors="pt",
        language=language,
    )
    inputs = inputs.to(model.device, dtype=model.dtype)
    t1 = time.perf_counter()

    with torch.inference_mode():
        outputs = model.generate(**inputs, max_new_tokens=MAX_NEW_TOKENS)

    torch.cuda.synchronize()
    t2 = time.perf_counter()

    text = processor.decode(outputs[0], skip_special_tokens=True)
    t3 = time.perf_counter()

    n_tokens = outputs.shape[-1]
    print(f"  chunk: preprocess={t1-t0:.3f}s  generate={t2-t1:.3f}s ({n_tokens} tok)  decode={t3-t2:.3f}s")
    return text

@asynccontextmanager
async def lifespan(app):
    load_model()
    yield

app = FastAPI(title="Cohere Transcribe", lifespan=lifespan)

@app.post("/v1/audio/transcriptions")
async def transcribe(
    file: UploadFile = File(...),
    language: str = Form("en"),
    model: str = Form(None),
):
    start = time.time()
    audio_bytes = await file.read()
    audio_array, sr = sf.read(io.BytesIO(audio_bytes))
    if audio_array.ndim > 1:
        audio_array = audio_array.mean(axis=1)
    audio_array = audio_array.astype(np.float32)
    duration = len(audio_array) / sr
    if duration < MIN_AUDIO_SECONDS:
        print(f"Rejected {duration:.2f}s clip (below {MIN_AUDIO_SECONDS}s)")
        return {"text": ""}
    text = transcribe_audio(audio_array, sr, language)
    elapsed = time.time() - start
    print(f"Transcribed {duration:.1f}s in {elapsed:.1f}s ({duration/elapsed:.1f}x realtime)")
    if not text:
        print("  -> empty after hallucination filter")
    return {"text": text}

@app.get("/health")
async def health():
    return {"status": "ok", "model": MODEL_ID, "device": str(DEVICE)}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("cohere_server:app", host="127.0.0.1", port=PORT)
