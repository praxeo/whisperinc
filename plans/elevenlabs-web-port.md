# Bringing elevenlabs-web's dictation work into WhisperInk — handoff

Status as of 2026-09-23 (evening), second session. Continue from here in a
session rooted in this repo.

## Goal and the one hard rule

WhisperInk is the desktop app to keep (global Ctrl+Space, warm mic + pre-roll,
direct paste, cloud and local CrispASR providers). The goal is to bring over the
reliability and accuracy work from `praxeo/elevenlabs-web`, the clinical
dictation web app.

**elevenlabs-web and its Cloudflare worker are read-only reference.** They run
in production at work. Read them for how problems were solved; never change them
for this effort. WhisperInk needs no backend — it calls ElevenLabs directly with
its own key.

Reference checked 2026-09-23: the local clone `C:\elevenlabs-web`, GitHub
`main` (`fa2b7b1`, 2026-09-15) and the deployed `eleven` worker (version 208,
deployed 30 s after that merge) all match — `cleanTranscript`,
`coverageShortfall` and the keyterm presets are byte-identical modulo the
bundler's escaping. Both repos are public.

## Where things stand

- **Merged.** Everything below is commit `fef3ba2` (on top of the Reson8 commit
  `b0476ca`), fast-forwarded onto `main` on GitHub 2026-09-23; PR #1 (Reson8)
  was marked merged by that push. The branches `feat/reson8-provider` and
  `fix/step0-reliability` are still on GitHub and can be deleted.
- **Running now: the test build `%USERPROFILE%\WhisperInk-step1\WhisperInk.exe`**
  (= `fef3ba2`), started 2026-09-23 19:22 in place of `_publish`.
- `_publish\WhisperInk.exe` (`1.0.0+176d4a6`, Aug 29) is the rollback and is
  now OLDER than `main`. To make `main` the everyday build: quit WhisperInk,
  then run `publish.ps1`. **Never run `publish.ps1` while WhisperInk is
  running** — it deletes `_publish\` first. (`WhisperInk-step0\` is the
  step-0-only build.) Two copies both answer Ctrl+Space — run one at a time.

### Done

**Step 0 (first session):** hook watchdog sweeps keyboard vkeys only; empty
text above the speech floor is a loud failure; CrispASR failures logged with
exit code + server output; `WriteCrash`/`RunSafe` log full exceptions.

**Deadlines:** `TranscriptionDeadline.cs` is wired in. Every transcription runs
under a per-take token (cloud 20 s + 20 s/min, cap 15 min; local/loopback 180 s
+ 1x audio, cap 2 h). Shared cloud client and CrispASR client → 2 h 10 min
backstop; Soniox poll ceiling → `CloudCap`, its DELETEs get their own 10 s.
Every cloud transcriber's cancel arm now separates "the take's deadline" from
"HttpClient timed out". CrispASR cancelled mid-inference → fresh server next take.

**1. ElevenLabs accuracy:** `language_code` (omitted for `auto`), `temperature=0`
unless configured, `diarize=false` + `num_speakers=1`,
`timestamps_granularity=word`; elevenlabs-web's `cleanTranscript`; word timing
exposed via `ITranscriptCoverage`. Gated on the new `ApiProvider.IsElevenLabs`
(xi-api-key or elevenlabs.io host), not "has a custom auth header".
Keyterm lists: seeded 2026-09-23 into the `elevenlabs` provider's
`ScribeKeytermsRaw` (local config.json only) — all three elevenlabs-web presets,
`standard` + `er` + `wound`, 228 terms after dedupe (backup:
`config.json.bak-keyterms-2026-09-24T00-21-52`). The wound list holds names and
common words (Greene, Kelly, Triad, Prisma); if ordinary words start coming out
as those, trim them in Providers → ElevenLabs.

**2. Never lose a dictation:** `UnsentTakes.cs` journals every take to
`%APPDATA%\.WhisperInk\unsent\` before the send; delivered → deleted; failed /
timed out / no text / incomplete / app error → kept with a reason; `pending` at
startup → `interrupted`; retention 14 days / 50 takes; startup balloon.
Menu **↻ Unsent dictations** → per take: Retry with the active provider, Retry
on each ready local model ("local fallback", Warn tone, model dropped after),
Show audio file. Retries go to the clipboard, never paste.
`debug.log` → `debug.previous.log` at launch; support bundle has both.

**3. Loud failures:** paste only when the take's own window is verified in front
(else clipboard + Warn + balloon); target captured when a take starts (a press
during transcription used to re-point it); `UiSound.Warn` (660 Hz ×2);
`TranscriptCoverage.cs` incomplete-transcript check (ElevenLabs); mic died
mid-take (`MicCapture.LastCaptureInterrupted` + immediate `onCaptureLost` alert),
mic stalled (capture ≥1 s shorter than the hold → reopen device), pure digital
silence → "No mic signal!"; press while busy → Dismissed blip.

Docs updated: CLAUDE.md (new section "Never lose a dictation, loud failures"),
README, docs/TRANSCRIPTION_ACCURACY_GUIDE.md, the settings dialog's keyterm text.

### Verified

- Release build: 0 warnings / 0 errors.
- `_scratch\crisp-harness` (`make-speech.ps1`, then `dotnet run`; `dotnet run --
  fast` skips crispasr): **74/74 pass** — deadline values; ElevenLabs request
  fields + response parsing against a fake server on 127.0.0.1:18999, incl. a
  16 s answer the old 15 s client failed; a non-ElevenLabs X-API-Key provider
  gets none of the Scribe fields; coverage on synthetic WAVs; the journal
  (deliver/keep/crash recovery/orphan WAV/retention); crispasr (CPU, ports
  18997/18998): startup failure, empty text, deadline mid-inference + restart,
  server killed between takes.
- **Live, first take on the test build (19:24):** ElevenLabs answered HTTP 200
  to the new field set (`language_code`, `temperature`, `diarize`,
  `num_speakers`, `timestamps_granularity`); 249 keyterms sent, none dropped;
  word timing read (`last word ends at 6.4 s; decoded 11.1 s`), no false
  coverage warning on a 10.7 s hold; deadline 24 s; pasted into the verified
  target; the journaled take was deleted on delivery.
- **Not exercised live yet:** the failure paths — a deadline firing, text left
  on the clipboard, Warn/mic-loss cues, the ↻ menu and a retry, startup
  recovery of an interrupted take (and step 0's watchdog fix and "No text!"
  cue). They only show when something actually goes wrong.

### Transcript audit (2026-09-23)

Checked for clinical transcripts on GitHub, read-only: every commit on every
branch of both public repos (whisperinc: 57 commits; elevenlabs-web: 162),
plus all PR/issue bodies and comments. **None found.** The only clinical-sounding
text is scripted test sentences (`_scratch/biasing/RECORD_THESE.md` prompts,
the TTS line in `make-speech.ps1`, synthetic harness/test strings); voice clips
and result folders were always gitignored. Where transcripts DO exist: locally
in `%APPDATA%\.WhisperInk\` (`debug.log`, `debug.previous.log`, `history.json`,
`unsent\`), and in the cloud via OneDrive — `Documents\MyRecordings\temp_audio.wav`
(the latest take's audio) is OneDrive-synced, as is anything on the Desktop
(e.g. a support bundle, which carries a `debug.log` tail).

The keyterm seeding script lived in the session scratchpad. If needed again,
the logic: evaluate `C:\elevenlabs-web\keyterms.js` (`KEYTERM_PRESETS`), dedupe
against the existing `ScribeKeytermsRaw` and `ContextBiasTerms`, write the rest
newline-joined into the `elevenlabs` provider's `ScribeKeytermsRaw` — with
WhisperInk NOT running (it rewrites config.json from memory), after a backup.

## Open questions for the user

- Should a local model eventually replace ElevenLabs, or stay a backup? (If
  replace: measure them against ElevenLabs on real recordings first.)
- Phone link in WhisperInk?
- Hotkey: Ctrl+Space only, or add CapsLock hold?
- Merge the Reson8 branch into `main` first, or keep building on it?
- `debug.log` (and so the support bundle) contains transcript text — redact?

## Gotchas learned

- Line endings are mixed per file (CRLF: MainWindow.xaml.cs, MicCapture.cs,
  UiSoundPlayer.cs, SupportBundle.cs, ProviderSettingsWindow.xaml.cs, …; most
  others LF). The Edit tool preserves them; count bytes to check.
- `debug.log` timestamps are time-only; the header line has the date.
- The crispasr servers on ports 8001 (Qwen3) and 8880 (Kokoro TTS), pinned to
  GPU 1, are not WhisperInk's — leave them alone. They run from the same
  `cohere-gguf\crispasr.exe`, so a CrispASR update affects them too.
- No Python on this machine; Node 24 is available.
- An XML comment can't contain `--` (it broke the harness csproj once).
- `speech.wav` from System.Speech has an 18-byte fmt chunk (46-byte header) —
  find the `data` chunk, don't assume 44.
- The fake-server tests run on loopback, so a provider pointed at them gets
  the LOCAL deadline budget — use a cloud provider object for cloud budgets.
- Out of scope here (elevenlabs-web, the user's call): the work AutoHotkey
  scripts force-stop any hold at 60 s (`HOLD_CAP_MS := 60000`).
