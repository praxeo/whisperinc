# Does the Cohere backend benefit from CrispASR biasing?

**Sprint:** investigation + decision (no app changes).
**Date:** 2026-06-12.
**Binary under test:** `%APPDATA%\.WhisperInk\cohere-gguf\crispasr.exe` (the #161 fix
build `4b27392f`, deployed 2026-06-12). Source read from the sibling clone
`OneDrive\Desktop\CrispASR\`.

---

## TL;DR decision

**No. The Cohere Transcribe (GGUF AED) backend under CrispASR does not honor
contextual biasing.** The `hotwords` request field is parsed by the server and
copied into the per-request params, but the Cohere backend never reads it — there
is no code path from `hotwords` to Cohere's decoder. This is settled at the source
level (below); the empirical A/B (Deliverables 2-3) is a **confirmation** step and
is the only part blocked, pending real voice recordings (see
`_scratch\biasing\RECORD_THESE.md`).

Recommended routes for the known OOV failure set (e.g. `hematochezia`) — neither
involves an LLM rewrite pass (explicitly rejected this sprint and in prior
feedback):

1. **Deterministic substitution dictionary** applied to the final transcript for a
   small, curated OOV set (exact/near-miss string map; not a model).
2. **Route must-catch terms to a provider with real keyterm biasing** — ElevenLabs
   Scribe v2 (`ScribeKeytermsRaw`), or the other verified vocabulary-steering
   providers (Google Chirp 3 `phraseSets`, Soniox `context.terms`, Cohere v2 cloud
   `cohere_terms`). On the **local** path, **Parakeet** is the only CrispASR backend
   with real phrase-boost (CTC-WS shallow fusion) — see the positive control.

---

## Deliverable 1 — Capability discovery

### 1.1 What `crispasr.exe --help` exposes

`crispasr.exe --help` (output goes to **stderr**, not stdout — worth knowing for any
harness). Decoding-guidance flags present:

- `--prompt PROMPT` — initial prompt (whisper-style; **max n_text_ctx/2 tokens**)
- `--grammar` / `--grammar-rule` / `--grammar-penalty` — GBNF constrained decoding
- `--suppress-regex` / `-sns,--suppress-nst` — token suppression
- `-bs,--beam-size N` (default 5), `-bo,--best-of N` (default 5)

**There is no `--hotwords` in the help text.** That is a documentation gap, not an
absence: the flag *is* parsed (see 1.3). No backend-gating text appears in `--help`.

### 1.2 Which backends actually consume `--hotwords` / `hotwords`

PLAN #98 ("Hotwords / contextual biasing") shipped in two phases, and the dispatch
is per-backend. Grepping the whole tree for the context-bias trie
(`core_context_bias` / `apply_bias` / `build_trie`) returns exactly **two** files:
`src/core/asr_context_bias.h` (the implementation) and `src/parakeet.cpp` (its only
consumer in `src/`).

| Mechanism | Backends that consume it | Evidence |
|---|---|---|
| **Phase A** — CTC-WS Aho-Corasick phrase-boost trie (shallow fusion on the logit stream) | **Parakeet** (TDT/CTC/RNNT/...) | `src/parakeet.cpp:209` holds `core_context_bias::Trie hotword_trie`, built by `parakeet_set_hotwords()` → `build_trie()` (`parakeet.cpp:2317,2367`); CLI/server wire it at `examples/cli/crispasr_backend_parakeet.cpp:122-128` |
| **Phase B** — hotword list injected into the decoder **prompt string** | **Voxtral**, **Qwen3-ASR** | `crispasr_backend_voxtral.cpp:106-108` (`"The following words may appear: " + p.hotwords`); `crispasr_backend_qwen3.cpp:231-235` (`"...may appear in the audio: " + params.hotwords`) |
| **(none)** | **Cohere** | `crispasr_backend_cohere.cpp` never references `hotwords`; `src/cohere.cpp` has **zero** matches for `hotword`/`context_bias`/`apply_bias` |

`src/core/asr_context_bias.h` is explicit that Phase A is a **CTC/TDT** technique —
its header comment: *"When the CTC/TDT decoder is scoring frame logits, the trie
adds a configurable boost to tokens that continue an active hotword prefix match …
the same 'shallow fusion' approach described in NeMo's CTC-WS word boosting and the
TurboBias paper."* The API is a frame-synchronous `apply_bias(logits)` + `advance(token)`
loop. Cohere is an **attention encoder-decoder** (Fast-Conformer encoder + Transformer
decoder, cross-entropy trained, autoregressive cross-attention decode) — it does not
run a frame-synchronous CTC/TDT argmax loop, so this mechanism has no hook on it.

The Cohere CLI/server adapter confirms it reads only sampling controls and never
hotwords (`examples/cli/crispasr_backend_cohere.cpp:69-81`):

```cpp
cohere_set_temperature(ctx_, params.temperature, params.seed);
cohere_set_beam_size(ctx_, params.beam_size > 0 ? params.beam_size : 1);
cohere_set_max_new_tokens(ctx_, params.max_new_tokens);
cohere_set_frequency_penalty(ctx_, params.frequency_penalty);
cohere_result* r = cohere_transcribe_ex(ctx_, samples, n_samples, params.language.c_str(), t_offset_cs);
// ^ no params.hotwords anywhere; CohereBackend declares no context-bias capability flag
```

**PLAN #98 says so directly.** Its backend support table classifies
`cohere-transcribe | NO | API takes model / language / file / temperature only`, and
its Phase-C "won't do" list reads: *"Cohere / Moonshine / Kyutai-STT / GLM-ASR — no
upstream support, no architectural hook; would require training a side-channel which
[is out of scope]."* The #98 closeout: *"CTC-WS Aho-Corasick trie wired into parakeet
CTC + TDT; LLM prompt injection for qwen3-asr + voxtral."* Cohere is named in neither.

> Note on Voxtral/Granite WhisperInk presets: only **voxtral** (3B) and **qwen3** read
> hotwords in the shipped CLI dispatch. `voxtral4b` and `granite` were *intended*
> Phase-B targets in the PLAN but do not appear in the hotword grep of
> `examples/cli/*.cpp` — treat their biasing as unverified, out of scope for this sprint.

### 1.3 Launch-time vs per-request

**Both — but moot for Cohere.**

- **Launch-time CLI:** `--hotwords`, `--hotwords-file`, `--hotwords-boost` are parsed in
  `examples/cli/cli.cpp:414-439` (into `params.hotwords` / `params.hotwords_boost`).
  They are simply omitted from `--help` text.
- **Per-request server:** `hotwords` and `hotwords_boost` are multipart form fields on
  **both** server endpoints — `crispasr_server.cpp:780-781` (`/v1/audio/transcriptions`)
  and `:917-918` (the legacy `/inference`). Documented in `docs/server.md:109-110,160-168`.
  The per-request struct is a full `whisper_params` (`whisper_params rp = params;`,
  `crispasr_server.cpp:765,896`) overlaid with form fields and passed straight to
  `backend->transcribe(..., rp)` (`crispasr_server.cpp:413`). So the per-request
  `hotwords` field *is* `params.hotwords` for whichever backend reads it.

WhisperInk's `CrispAsrServerTranscriber.cs:146-149` already sends `hotwords` (and an
optional `beam_size`) as per-request fields whenever bias terms exist. On Parakeet that
does real work; **on Cohere the field lands in `rp.hotwords` and is never read.**

---

## Deliverable 2 — A/B harness

`_scratch\biasing\_hotwords_ab.ps1` (standalone; no app changes). It mirrors
`CrispAsrServerTranscriber.cs` exactly: spawn `crispasr.exe --server` → poll `/health`
(120 s) → multipart POST to `/v1/audio/transcriptions` with
`language` / `beam_size` / `response_format=json` / optional `hotwords` / `file` → parse
`.text`.

**Design note — deviates from the sprint's "two servers" suggestion, by discovery.**
The sprint assumed `--hotwords` was launch-time-only and prescribed Server A (no
hotwords) vs Server B (with hotwords). Source analysis (1.3) showed `hotwords` *and*
`beam_size` are per-request fields, so toggling them per request on one server is valid
and is what the app actually does. To keep the isolation the two-server design was
protecting against, the harness still **relaunches a fresh server for every cell**
(`backend x beam x hotwords`) and captures each server's own stderr — generalizing
"Server A vs Server B" to all 8 cells, with zero shared state.

Holds constant across cells: model quant, GPU backend (`-GpuBackend`, default `auto`),
thread count (`min(8, cores)`), language (`en`). Outputs `rows.csv`, `rows.json`,
per-cell `*.stderr.log`, and a paste-ready `summary.md` under
`_scratch\biasing\results\<timestamp>\`.

**Scoring.** Per clip × cell: per-term hit (exact target term present, word-boundary,
case-insensitive); spurious-injection (any *other* lexicon term appearing); and for
Cohere, per-clip normalized text equality off-vs-on at each beam (the no-op signal).

**Status: authored, not yet run — blocked on recordings.** No `*.wav` exist anywhere in
the repo and `_scratch\biasing\clips\` is empty. Per guardrail the harness STOPs and
prints the manifest rather than substituting TTS.

---

## Deliverable 3 — Controls (pending the run)

1. **Positive control — Parakeet TDT.** Same harness, `parakeet-tdt-0.6b-v3-q4_k.gguf`,
   `--backend parakeet`. Parakeet genuinely builds the phrase-boost trie
   (`parakeet.cpp:209,2367`), so a target-hit lift and/or changed text when hotwords are
   on proves the harness exercises real biasing — isolating "Cohere unsupported" from
   "flag/harness broken."
2. **Behavior/log diff.** Each cell's stderr is scanned for any `hotword` line. Caveat:
   `parakeet_set_hotwords()` has **no** "hotwords loaded: N" printf in the source, so an
   explicit load-count line may not exist; the **token-for-token diff** (Cohere identical
   off vs on) is therefore the primary evidence, the log scan secondary.
3. **Beam × hotwords matrix.** `{beam 1, beam 5} × {off, on}` is run for **both**
   backends — shallow fusion has the most room to act when beam search is exploring, so
   both are reported.

### Empirical A/B results

> PENDING. Record the clips in `_scratch\biasing\RECORD_THESE.md`, run
> `_scratch\biasing\_hotwords_ab.ps1`, then paste the generated `summary.md` table here.
> Expected per source analysis: Cohere `Text changed off->on` = 0/N at both beams;
> Parakeet shows a non-zero target-hit lift and/or changed text.

| Backend | Beam | Target hit off | Target hit on | Control hit off | Control hit on | Text changed off->on | Spurious inj (on) |
|---|---|---|---|---|---|---|---|
| cohere | 1 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |
| cohere | 5 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |
| parakeet | 1 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |
| parakeet | 5 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

---

## Deliverable 4 — Decision

**Does Cohere-under-CrispASR honor biasing? No.**

- **Evidence (source, conclusive):** the Cohere backend adapter
  (`crispasr_backend_cohere.cpp`) and core (`src/cohere.cpp`) contain no reference to
  `hotwords` / context-bias; the only trie consumer is `parakeet.cpp`; the only
  prompt-injection consumers are voxtral/qwen3; PLAN #98 explicitly classifies Cohere as
  "NO — no upstream support, no architectural hook." Code that never reads the field
  cannot be influenced by it.
- **Evidence (empirical, pending):** token-for-token Cohere off-vs-on identity at beam 1
  and beam 5, alongside a demonstrable Parakeet effect, will confirm it. Harness ready;
  blocked only on voice clips.

**Because the answer is No, the acceptable routes for the must-catch OOV set are:**

1. **Deterministic substitution dictionary** — a curated exact/near-miss string map
   applied post-transcription for the small known-failing set (e.g. `hematochezia`). Not
   a model, not an LLM pass; fully predictable, no rewriting of correct dictation.
2. **Route must-catch terms to real keyterm biasing** — ElevenLabs Scribe v2
   (`ScribeKeytermsRaw`) is the strongest; Google Chirp 3, Soniox, and Cohere v2 cloud
   also do genuine vocabulary steering. Locally, only **Parakeet** offers real phrase
   boost via CrispASR.

**Explicitly rejected:** any LLM post-processing/correction pass (rewrites dictation;
rejected this sprint and in prior user feedback).

> Wiring biasing into the app is a separate, follow-up sprint and is **gated on a
> positive result** — which this investigation does not produce for Cohere. No app
> wiring was done here.

---

## Appendix — source citations (sibling clone `OneDrive\Desktop\CrispASR\`)

- `src/core/asr_context_bias.h` — CTC/TDT shallow-fusion trie; header states CTC/TDT scope.
- `src/parakeet.cpp:209,2317,2367` — `hotword_trie` field + `parakeet_set_hotwords` + `build_trie` (only `src/` consumer).
- `examples/cli/crispasr_backend_parakeet.cpp:122-128` — Phase A wiring.
- `examples/cli/crispasr_backend_voxtral.cpp:106-108`, `crispasr_backend_qwen3.cpp:231-235` — Phase B prompt injection.
- `examples/cli/crispasr_backend_cohere.cpp` (whole file) — reads temp/seed/beam/max_new_tokens/freq_penalty; **no hotwords**.
- `src/cohere.cpp` — zero `hotword`/`context_bias` matches.
- `src/crispasr_c_api.cpp` — zero `hotword` matches (no generic application path).
- `examples/cli/crispasr_server.cpp:780-781,917-918` (hotwords form field), `:792,924` (beam_size form field), `:765,896` (`whisper_params rp = params;`), `:413` (`backend->transcribe(..., rp)`).
- `examples/cli/cli.cpp:414-439` — `--hotwords` / `--hotwords-file` / `--hotwords-boost` CLI parsing.
- `docs/server.md:109-110,160-168` — `hotwords` / `hotwords_boost` per-request form fields.
- `PLAN.md` #98 — support table (`cohere-transcribe | NO`), Phase A+B closeout, Phase-C "won't do" (Cohere).
