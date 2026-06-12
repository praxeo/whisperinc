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
is no code path from `hotwords` to Cohere's decoder.

**Confirmed both ways (2026-06-12).** Source: the Cohere backend never reads
`params.hotwords` (below). Empirical: with real recordings in the user's voice, at
`hotwords_boost 20` — a boost that rewrote **6/6** of Parakeet's transcripts (the
positive control) — Cohere stayed **token-for-token identical** off-vs-on across
both beams (0/6 changed). The field is dropped regardless of boost.

Secondary practical finding: even **Parakeet's** biasing (the only local backend
that honors hotwords) is a blunt instrument here. The default boost (2.0, what
WhisperInk currently sends) moved nothing; the boost needed to flip `hematochezia`
(~18-20) garbles neighboring words and dropped controls from 2/3 to 0/3. So local
hotwords is not a clean fix for hard medical OOV terms on *any* backend.

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

**Status: authored + run (2026-06-12).** Ran twice on the user's 6 real clips
(2x `hematochezia` + `ureterolithiasis` / `biliary colic` / `ureteral colic` + neutral),
48 kHz stereo PCM: once at the default boost (2.0) and once at boost 20. Results below.

---

## Deliverable 3 — Controls (done)

1. **Positive control — Parakeet TDT.** Same harness, `parakeet-tdt-0.6b-v3-q4_k.gguf`,
   `--backend parakeet`. **Established.** At `hotwords_boost 20` the Parakeet server
   path changed **6/6** clips (both beams) and produced the correct `hematochezia`
   spelling — so the harness demonstrably exercises real biasing. At the same boost
   Cohere changed 0/6. This isolates "Cohere unsupported" from "flag/harness broken."
   (Independently reproduced via launch-time CLI `--hotwords` — see boost sweep below.)
2. **Behavior/log diff.** Per-cell stderr scanned for any `hotword` line: **none** in any
   cell (as predicted — `parakeet_set_hotwords()` has no load-count printf). So the
   **token-for-token diff** is the evidence: Cohere identical off-vs-on at every boost;
   Parakeet rewrites its output when biasing is applied at a non-trivial boost.
3. **Beam × hotwords matrix.** `{beam 1, beam 5} × {off, on}` run for both backends at two
   boosts (2.0 default, 20). Cohere is invariant across the entire matrix; Parakeet only
   moves at the high boost. Tables below.

### Empirical A/B results

Two passes on the user's 6 real clips, GPU backend `auto` (CUDA, 3x RTX 3090/3080), beam
set at launch *and* per request.

**Pass A — `hotwords_boost 2.0` (server default; what WhisperInk currently sends):**

| Backend | Beam | Target hit off | Target hit on | Control hit off | Control hit on | Text changed off->on | Spurious inj (on) |
|---|---|---|---|---|---|---|---|
| cohere   | 1 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |
| cohere   | 5 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |
| parakeet | 1 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |
| parakeet | 5 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |

At the default boost *nothing* moves — not even Parakeet. This pass alone cannot
distinguish "no-op" from "too weak," which is why pass B raises the boost.

**Pass B — `hotwords_boost 20`:**

| Backend | Beam | Target hit off | Target hit on | Control hit off | Control hit on | Text changed off->on | Spurious inj (on) |
|---|---|---|---|---|---|---|---|
| cohere   | 1 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |
| cohere   | 5 | 0/2 | 0/2 | 2/3 | 2/3 | **0/6** | 0 |
| parakeet | 1 | 0/2 | 0/2* | 2/3 | 0/3 | **6/6** | 0 |
| parakeet | 5 | 0/2 | 0/2* | 2/3 | 0/3 | **6/6** | 0 |

\* **Scorer artifact, not a Parakeet failure.** Over-boost fuses the word as
`withhematochezia`, which the strict word-boundary matcher counts as a miss; the raw
transcripts plainly contain `hematochezia` (`rows.csv`). The decisive contrast is
**Text changed: Parakeet 6/6 vs Cohere 0/6** at the identical boost. Parakeet's controls
falling to 0/3 is the over-boost garbling cost (e.g. `biliary colic` ->
`withbiliarycolicure.ureteralureter`).

**Independent CLI boost sweep (Parakeet, launch-time `--hotwords`, beam 5):**

| Clip | boost 2 | boost 10 | boost 20 | boost 25 |
|---|---|---|---|---|
| `hematochezia_1` | hematoche**s**ia | hematoche**s**ia | hemato­che**z**ia (correct) | hematochezia (correct) + heavy garble |
| `ureterolithiasis` | "ureter with ISIS" | "**uureteral** with ISIS" | — | full garble (ureteral×N) |

Confirms the effect is boost-dependent and real on Parakeet via *both* the launch-time
and per-request paths. Cohere never changed at any boost on either path.

**Addendum — can the Parakeet bluntness be tuned out? Collateral yes, reliability no.**
The trie applies a *flat additive* boost to the logit of any token continuing a hotword
prefix, and the first sub-word token of every listed hotword is boosted on every step
(`asr_context_bias.h` `apply_bias` walks the root's children unconditionally). So with
all four terms boosted at 20, `hemato`/`ureter`/`bili` get slammed everywhere — that
self-inflicted the control garbling in Pass B. Narrowing the list fixes that:

| Clip (beam 1) | base | `hematochezia`-only @16 | `hematochezia`-only @18 | all-4 @18 |
|---|---|---|---|---|
| `hematochezia_1` | hematoche**s**ia | hematoche**s**ia | hemato**hemsia** (mangled) | hemato**hemsia** |
| `hematochezia_2` | hematoche**s**ia | hemato­che**z**ia (correct) | recthemato … hematochezia | bloodure perurecthemato … |
| `biliary_colic` (control) | biliary colic | **biliary colic (clean)** | **biliary colic (clean)** | withbiliarycolic.ureteralureter |

So: (1) **collateral on other words is tunable** — narrow the list and/or use the
per-term `word^N` boost suffix (the trie parses it; passes through the server `hotwords`
field), and unlisted words stay clean. (2) **The target term is per-utterance
unreliable** — `hematochezia_2` flips at boost 16 while `hematochezia_1` only mangles to
`hematohemsia`; a flat additive boost cannot calibrate to per-instance acoustic margin,
and CrispASR exposes no gated/beam-rescored fusion. Net: Parakeet hotwords can be made
*non-destructive*, but not *dependable* for a must-catch term.

---

## Deliverable 4 — Decision

**Does Cohere-under-CrispASR honor biasing? No.**

- **Evidence (source, conclusive):** the Cohere backend adapter
  (`crispasr_backend_cohere.cpp`) and core (`src/cohere.cpp`) contain no reference to
  `hotwords` / context-bias; the only trie consumer is `parakeet.cpp`; the only
  prompt-injection consumers are voxtral/qwen3; PLAN #98 explicitly classifies Cohere as
  "NO — no upstream support, no architectural hook." Code that never reads the field
  cannot be influenced by it.
- **Evidence (empirical, confirmed 2026-06-12):** token-for-token Cohere off-vs-on
  identity at beam 1 and beam 5 at both boost 2.0 and boost 20 (0/6 changed), while the
  Parakeet positive control changed 6/6 and produced the correct `hematochezia` at boost
  20 — proving the harness delivers hotwords and isolating Cohere as the unsupported
  backend, not a broken flag/harness.

**Because the answer is No, the acceptable routes for the must-catch OOV set are:**

1. **Deterministic substitution dictionary** — a curated exact/near-miss string map
   applied post-transcription for the small known-failing set (e.g. `hematochezia`). Not
   a model, not an LLM pass; fully predictable, no rewriting of correct dictation.
2. **Route must-catch terms to real keyterm biasing** — ElevenLabs Scribe v2
   (`ScribeKeytermsRaw`) is the strongest; Google Chirp 3, Soniox, and Cohere v2 cloud
   also do genuine vocabulary steering. Note the **cloud** Cohere v2 API supports
   keyterms even though the **local** Cohere GGUF backend does not.

Locally, **Parakeet** is the only backend that honors hotwords, but it still can't
*guarantee* a hard OOV term. Collateral is tunable — narrowing the list to the target
term and/or per-term `word^N` boosts (the trie parses the suffix; it rides the server
`hotwords` field) keeps other words clean (`biliary colic` stays perfect). But the target
is **per-utterance unreliable**: `hematochezia_2` flips at boost 16 while `hematochezia_1`
only mangles to `hematohemsia`. A flat additive logit boost can't calibrate to
per-instance acoustic margin, and CrispASR exposes no gated/beam-rescored fusion. So
"just bias Parakeet" is non-destructive at best, not dependable.

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
