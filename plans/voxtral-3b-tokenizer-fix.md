# Fixing CrispASR's Voxtral 3B tokenizer, and proposing the fix upstream — brief

Written 2026-09-25 for the next session. The owner wants to fix this bug in
CrispASR themselves and propose the fix to its maintainer. Read CLAUDE.md
Part 1 first, as always, then this.

**Update, later on 2026-09-25:** the owner filed the bug instead, as
[CrispStrobe/CrispASR#472](https://github.com/CrispStrobe/CrispASR/issues/472),
with the jfk.wav repro and the suggested fix below. Before doing anything
else, read #472's thread: the maintainer may already have fixed it (they did
#338 within a day). If they have, go to "After it's released". The rest of
this brief is for sending the fix as a pull request, if the owner still wants
to; link it to #472.

**Update, 2026-09-26:** the maintainer fixed it on `main` in `471fb14d`
and closed #472. There's no pull request to send. Go straight to "After it's
released" (CLAUDE.md 5.1 lists what else that release changes).

## Goal

1. Fix the bug in CrispASR's Voxtral 3B speech-to-text runtime
   (`src/voxtral.cpp`), on a branch of the sibling clone.
2. Prove it on this machine: before and after, on CrispASR's own sample and on
   the owner's clinical clips with the full bias list.
3. Propose it upstream as a pull request from the owner's GitHub account
   (`praxeo`), after showing the owner the final title and description.
4. Record the results in CLAUDE.md (4.3, 10.1, 10.2), with dates and method.

## The bug, as measured 2026-09-24/25

- **Symptom.** `voxtral-local` (Voxtral Mini 3B,
  `%APPDATA%\.WhisperInk\cohere-gguf\voxtral-mini-3b-2507-q4_k.gguf`) with the
  owner's 21-term shared list: every take, the `neutral` clip included, comes
  back as ~520 `<unk>` (2,600 characters) after ~6 s. WhisperInk would paste
  that with the Success tone (CLAUDE.md 10.2). One term at a time, only
  "epigastric" does it; every other term, and lists without it, transcribe.
- **Cause.** `src/voxtral.cpp` builds the map its BPE merge searches
  (`tekken_build_reverse`) from every serialized Tekken entry (150,000,
  `tokenizer.tekken.n_vocab`), but the model's embedding table has
  `voxtral.llm.vocab_size` = 131,072 rows: 1,000 specials plus 130,072
  entries. A word whose merges reach the tail gets an id of 131,072 or more,
  and the embedding lookup reads out of bounds.
  - "epigastric" → `ep` 2003, `ig` 1351, `astric` **146,371**: the `<unk>` flood.
  - ",syncopal" → `opal` **131,628**: no visible damage here, but the same
    out-of-bounds read. On CPU, #338 saw an assertion.
  - `_scratch\biasing\_tekken_ids.ps1` replays `src/voxtral.cpp`'s BPE against
    the GGUF's own vocabulary and flags each list term that gets such an id.
- **Prior art upstream (#338, August 2026, on the TTS runtime).**
  - `83db1d7a` added `src/voxtral_tekken_vocab.h` (namespace `voxtral_tekken`:
    `active_bpe_count()`, `token_id_in_range()`, `decode_blob()`), used by
    `src/voxtral_tts.cpp` and `src/voxtral4b.cpp`, with
    `tests/test-voxtral-tekken-vocab.cpp`.
  - `c69ac61b` moved a pre-tokenizer that matches mistral-common into the same
    header (`pre_tokenize()`), used by `voxtral_tts.cpp` only, with
    `tests/test-voxtral-pretokenize.cpp` and
    `tools/check-voxtral-tokenizer-parity.py`.
  - Neither touched `src/voxtral.cpp`. It still has its own unbounded map and
    its own old `tekken_pre_tokenize` (line ~1450 on `main`, 2026-09-25).
  - The maintainer implemented #338's fix themselves within a day of the
    report. That's why this one wasn't filed as an issue: the owner wants to
    propose the fix.
- **Where it is.** In v0.8.30 (deployed here), v0.8.36 (the latest release on
  2026-09-25) and `main`. **Re-check `main` first**: if it has been fixed
  meanwhile, skip to "Verify" and "After it's released".

## Where to work

- The sibling clone `..\CrispASR`, next to this repo.
  It sits at `9eecfd43` (2026-06-13) with **one uncommitted local patch**
  (ggml-blas PkgConfig optional; CLAUDE.md 5.6). Keep it: `git stash` before
  switching branches, and check whether `main` still needs it to build here.
- `git fetch`, then branch from upstream `main`
  (e.g. `fix/voxtral-3b-tekken-vocab`).
- For the pull request: `gh repo fork CrispStrobe/CrispASR` into `praxeo` and
  push the branch there. `gh` is authenticated as `praxeo`.
- Read upstream's `CLAUDE.md` and any contribution notes first, and follow
  their conventions (commit style, formatting, how tests are registered).

## The fix, to confirm against the code

Do for `src/voxtral.cpp` what #338 did for the TTS and 4B runtimes:

1. Include `voxtral_tekken_vocab.h`. Admit only
   `active_bpe_count(llm_vocab_size, n_specials)` entries to the map the BPE
   merge searches. Keep whatever the id-to-text path needs (check how
   `voxtral_token_text` uses `rank_offset`).
2. Check every prompt id with `token_id_in_range()` before the embedding
   lookup, as `83db1d7a` did, and say so on stderr when one is dropped.
3. Replace `tekken_pre_tokenize` with the header's `pre_tokenize()`, so the 3B
   gets `c69ac61b`'s parity fixes too. A separate commit if that reads better.
4. Tests in the style of the existing weight-free ones: extend
   `tests/test-voxtral-tekken-vocab.cpp` / `test-voxtral-pretokenize.cpp`, or
   add one for the 3B path, with a word whose merges reach the tail.

Keep the diff small and in the maintainer's style.

## Build without touching the deployed binary

- **Never replace `%APPDATA%\.WhisperInk\cohere-gguf\crispasr.exe`.** It's the
  tested v0.8.30, and the owner's own servers on ports 8001 and 8880 run from
  it. Don't run `scripts\build-crispasr.ps1` either: it copies into that
  folder with no backup (CLAUDE.md 5.6).
- Build in the clone's own build folder: the VS developer shell,
  `-G "Visual Studio 17 2022" -A x64`, CUDA on, then
  `cmake --build build --config Release --target crispasr-cli` and the test
  targets. `-DWHISPER_BUILD_TESTS=OFF` avoided a Catch2 download before; see
  how `tests/CMakeLists.txt` builds the voxtral tests.
- Build `main` unmodified first (the "before" binary), then the fix. Copy
  each `crispasr.exe` with **all** its DLLs into its own scratch folder and
  run it from there, on an unused port (`_local_bias_ab.ps1` uses
  18207–18217; the crisp-harness uses 18993–18998).
- This machine has no Python, so `tools/check-voxtral-tokenizer-parity.py`
  (mistral-common) can't run here unless the owner wants Python installed.
  Say in the PR that it wasn't run.

## Verify

Record every number with its date and method (CLAUDE.md "Maintaining this
file").

1. **Tokenizer.** The new tests pass, and fail on the "before" code.
2. **CrispASR's own sample.** `samples/jfk.wav` with
   `--hotwords "epigastric"` on the 3B GGUF: before, the `<unk>` flood (or the
   CPU assertion); after, the JFK sentence.
3. **No side effects.** Without hotwords, the fixed binary's transcripts are
   identical to the "before" binary's on jfk.wav and the six clips.
4. **The clinical question.** Give `_scratch\biasing\_local_bias_ab.ps1` an
   `-Exe` parameter (it always runs the deployed exe today), then run
   `-Only 3b` against the fixed binary with the full 21-term list, plus
   `-Extra .\joined`. Compare with the 2026-09-24 rows in CLAUDE.md 4.3: with
   19 terms, 5/6 short clips (ureterolithiasis missed) and 6/6 on the 32 s
   takes; Qwen3 with the list, 6/6 everywhere. Also try one take over 60 s
   (`_join_clips.ps1 -Tails`): Voxtral 3B chunks internally, so #471 (the
   silent-piece bug) shouldn't apply, but check.
5. **Speed**, warm, in the same run as the "before" binary.

## Propose it

- Show the owner the pull request's title and description before posting:
  it's public and in their name.
- The description: the symptom with a public repro (jfk.wav plus
  `--hotwords "epigastric"`), the cause with the numbers above, "the same fix
  as #338, applied to the 3B runtime", what the tests cover, and the
  before/after results. Credit #338's analysis.
- No clinical audio, transcripts or personal names in it.

## After it's released

- A/B the release that carries the fix against v0.8.30 before deploying it
  (CLAUDE.md 5.2). Deploying replaces the exe the owner's other servers use
  too.
- Then Voxtral 3B can run with the list. Decide with the owner whether it
  earns a place (CLAUDE.md 4.2): it's the only local model that got the hard
  terms without a list, but it wrote "urethral colic" for "ureteral colic" on
  a short clip.

## Also on the table (ask the owner)

- WhisperInk: treat a transcript containing `<unk>` as a failed request
  (Error tone, take kept for a retry) instead of pasting it (CLAUDE.md 10.2).
  Small, and independent of the upstream fix.
