# Record these clips before running the hotwords A/B

The A/B harness (`_hotwords_ab.ps1`) needs **real recordings in your own voice**.
Do **not** use TTS: the `hematochezia` failure is acoustic/speaker-specific, and a
synthetic voice would not reproduce the OOV miss we are trying to bias away — it
would invalidate the whole test.

## Where
Save each file as a **mono WAV** into:

```
_scratch\biasing\clips\
```

16 kHz mono PCM is ideal (matches the app's capture path), but any reasonable
sample rate is fine — CrispASR resamples internally. Record the way you normally
dictate (same mic, normal pace). One short sentence per file; the **bold** term
must be spoken naturally inside the sentence.

Easiest tools: Windows **Sound Recorder** (export/convert to .wav), **Audacity**
(Export → WAV), or any recorder that can save WAV.

## The clips (5 required + 1 optional)

| File name | Read this sentence aloud | Role |
|---|---|---|
| `hematochezia_1.wav` | "The patient presented with **hematochezia** and mild abdominal pain." | target OOV (rep 1) |
| `hematochezia_2.wav` | "On exam there was bright red blood per rectum, consistent with **hematochezia**." | target OOV (rep 2) |
| `ureterolithiasis.wav` | "CT confirmed **ureterolithiasis** on the left side." | control (usually correct) |
| `biliary_colic.wav` | "Her symptoms were consistent with **biliary colic**." | control (usually correct) |
| `ureteral_colic.wav` | "He reported severe **ureteral colic** radiating to the groin." | control (usually correct) |
| `neutral.wav` (optional) | "The patient was discharged home in stable condition." | spurious-injection probe (no lexicon term) |

Two `hematochezia` takes give n=2 on the term that actually matters. The three
controls are terms that already transcribe correctly, so biasing must not break
them. `neutral.wav` contains none of the hotwords — if a hotword shows up there
under biasing, that is a spurious-injection regression.

## Then run

```powershell
cd _scratch\biasing
.\_hotwords_ab.ps1                 # full matrix on the deployed GPU build (auto)
# .\_hotwords_ab.ps1 -GpuBackend cpu   # if you want the CPU path instead
```

It runs `{cohere, parakeet} x {beam 1, beam 5} x {hotwords off, on}`, spawning a
fresh `crispasr.exe --server` per cell, and writes results under
`_scratch\biasing\results\<timestamp>\` (rows.csv, rows.json, per-cell stderr
logs, and `summary.md`). Paste `summary.md` into
`plans\cohere-biasing-findings.md` under "Empirical A/B results".

What to expect if the source analysis is right: **Parakeet** (positive control)
shows a target-hit lift and/or changed text when hotwords are on; **Cohere** is
token-for-token identical off vs on (0/N changed) at both beams — i.e. the field
is silently ignored.
