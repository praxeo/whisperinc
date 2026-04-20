# WhisperInk — Packaging & Polish Sprint

## Context

- **Repo:** `C:\Users\John Obert\Documents\GitHub\whisperinc` (remote: `praxeo/whisperinc`, branch: `main`)
- **Stack:** C# / WPF / .NET 8 Windows. WPF + Win32 hooks for a system-wide dictation hotkey (Ctrl+Space). Single-executable app, no installer today.
- **What it does:** Global hotkey → records audio → transcribes via one of several providers (Mistral, OpenAI, ElevenLabs, Cohere cloud, or several local CrispASR servers including Cohere Q4/Q6_K and Parakeet) → pastes text into the active window.
- **State on disk:** `%APPDATA%\.WhisperInk\` holds `config.json`, `debug.log`, and the `cohere-gguf\` model/binary folder. Do not change that layout.
- **Current launch method:** `dotnet run` from the repo, or manually running `bin\Release\net8.0-windows\WhisperInk.exe` after `dotnet publish`. Hard to find, no Start Menu entry, no desktop shortcut, no tray menu for support.
- **Conventions you should already pick up from `CLAUDE.md`** in the repo root — read it first.

## Goal

Make WhisperInk easy to find, launch, keep running, and self-diagnose — without touching the transcription pipeline or provider logic.

## Deliverables

Work through these in order. Commit each as its own focused commit on `main` with a short, direct message. Before each commit, `dotnet build -c Release` must succeed with zero warnings.

### 1. Executable metadata + icon

- Set proper `AssemblyTitle`, `AssemblyDescription`, `AssemblyCompany`, `AssemblyVersion`, `FileVersion`, and `AssemblyInformationalVersion` in `AssemblyInfo.cs` (or via `<PropertyGroup>` in the csproj — whichever fits the existing style).
- Embed an application icon. If `App.ico` or similar doesn't exist, generate a simple one (solid color + a single-letter "W" or similar glyph) and wire it through `<ApplicationIcon>` in the csproj. This icon should show up on the taskbar, Alt+Tab, Start Menu, and File Explorer.
- Set `AssemblyInformationalVersion` to include the short git commit hash (e.g. `1.0.0+30f9fde`). Drive this from a pre-build MSBuild target that runs `git rev-parse --short HEAD`.

### 2. Publish + install scripts

Two existing scripts (`publish.ps1`, `publish-framework-dependent.ps1`) are the starting point. Don't delete them — improve them and add new ones.

- `scripts\install-shortcuts.ps1` — creates a Start Menu shortcut and an optional Desktop shortcut pointing at the published Release `WhisperInk.exe`. Use `WScript.Shell` COM (standard pattern). Take a `-Desktop` switch parameter; default is "Start Menu only". Idempotent — re-running replaces existing shortcuts cleanly.
- `scripts\install.ps1` — orchestrator: runs `publish.ps1`, then `install-shortcuts.ps1 -Desktop`, then prints next steps (e.g. "Run WhisperInk from the Start Menu" or "Pin to taskbar from the shortcut's right-click menu").
- `scripts\uninstall.ps1` — removes shortcuts and optionally the published exe. Does NOT touch `%APPDATA%\.WhisperInk\`.

### 3. System tray icon with support menu

This is the biggest UI deliverable. Use `System.Windows.Forms.NotifyIcon` (add the `UseWindowsForms` enable flag to the csproj). Icon appears on app start, persists in the tray, right-click shows a context menu:

- **Show Window** (restores/focuses the main window)
- **Active provider: <name>** (disabled label showing current ActiveProviderId with a health indicator — see Deliverable 4)
- **───**
- **Open debug log** (opens `%APPDATA%\.WhisperInk\debug.log` in the default text editor via `Process.Start` with `UseShellExecute=true`)
- **Open config folder** (opens `%APPDATA%\.WhisperInk\` in Explorer)
- **Open model folder** (opens `%APPDATA%\.WhisperInk\cohere-gguf\` in Explorer)
- **Copy support bundle** (see Deliverable 5)
- **───**
- **About…** (dialog: app version, build date, commit hash, .NET version, OS version, clickable link to README on GitHub)
- **View README** (opens `https://github.com/praxeo/whisperinc/blob/main/README.md` in default browser)
- **───**
- **Quit**

Left-click on the tray icon = Show Window. Double-click = same.

The main window's X button should minimize to tray, not exit. Add a "Settings → Quit on close" preference (persisted in config.json) for users who prefer traditional close behavior; default is "minimize to tray".

### 4. Provider health indicator

In the tray menu's "Active provider" label and in the main window status bar, show a dot:

- 🟢 green = provider is reachable (for HTTP providers: `/health` returns 200; for cloud providers: API key present; for local providers: `crispasr.exe` + model file both exist on disk)
- 🟡 yellow = unknown / not yet probed
- 🔴 red = failed last probe

Probe on app start, on provider switch, and every 60s while running. Cache results; don't block the UI thread.

For the local Cohere providers specifically, "reachable" also means the auto-spawned `crispasr.exe` is responding on its port if it's been spawned. Don't spawn it just for the health check — only report based on what's already running.

### 5. Support bundle

Tray menu → "Copy support bundle" → copies a zip to the clipboard (as a file reference) AND saves a copy to `%USERPROFILE%\Desktop\WhisperInk-support-<timestamp>.zip` containing:

- Last 500 lines of `debug.log`
- `config.json` with the `ApiKey` fields redacted (`***redacted***`)
- `about.txt` with app version, commit hash, .NET version, OS version, list of installed provider IDs, and which providers have local model files present
- NOT the model GGUFs (too big) and NOT the API keys (sensitive)

This is the single most valuable debug artifact and should be one click to produce.

### 6. First-run / diagnose flow

On app start, check:

1. Is there a `config.json`? If not, create one with defaults and note first-run.
2. For the currently active provider: does it have what it needs? (API key for cloud, model files for local)
3. If something's missing, show a non-modal "Setup needed" notification (tray balloon + main window banner) with a "Fix it" button that opens the relevant folder or settings dialog.

Add a tray menu item **"Diagnose active provider"** that runs the same checks on-demand and prints results to a dialog. Output format:

```
Provider: Cohere Local Q4 (CrispASR)
  crispasr.exe:                    FOUND   (1.1 MB, v0.4.12-era)
  cohere-transcribe-q4_k.gguf:     FOUND   (1.2 GB)
  ggml-vulkan.dll:                 FOUND   (56.8 MB — GPU acceleration available)
  Port 8104 reachable:             YES (server responded in 23ms)
```

### 7. README polish

- Add a "Quick install" section at the top showing the three-command flow: `git clone` → `.\scripts\install.ps1` → launch from Start Menu.
- Add a "Getting help" section pointing at the tray menu → "Copy support bundle" workflow.
- Link to `plans\packaging-polish-prompt.md` from a "Design docs" section (you're extending this doc, it should be discoverable).
- Do not rewrite existing content. Additive only.

### 8. Auto-start (optional, feature-flagged)

Add a Settings checkbox: **"Launch WhisperInk when Windows starts"**. Implement via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry entry (user-level, no admin needed). Unchecked by default.

## Guardrails — do NOT touch

- `CrispAsrServerTranscriber.cs`, `CohereOnnxTranscriber.cs`, `CohereGguf*Transcriber.cs` — the transcription pipeline is working and out of scope.
- Provider list in `AppConfig.cs` `CreateDefaults()` — don't add, remove, or rename providers.
- Hotkey behavior (Ctrl+Space for Batch, Ctrl+Alt for AnalyzeContext) — unchanged.
- `%APPDATA%\.WhisperInk\` folder layout — don't move things around.
- `MainWindow.xaml.cs` transcription dispatch blocks (anything inside the provider-ID guards). Reading them for context is fine; editing them is not.
- Do not add new providers. Do not remove any existing ones. Do not change provider IDs.

## Conventions

- **Terse commit messages.** Format: `<area>: <what changed>`. Example: `tray: add NotifyIcon with support menu`. No emoji. No "this commit…".
- **Zero warnings at build time.** Current Release build compiles clean; keep it clean.
- **Minimal dependencies.** Prefer .NET BCL and Win32 P/Invoke over NuGet packages. If you genuinely need a package, justify it in the commit message.
- **PowerShell scripts:** `$ErrorActionPreference = "Stop"` at the top. Absolute paths via `$env:APPDATA`, `$PSScriptRoot`, etc. — no relative paths. `curl.exe` for downloads (already installed on Win10+), not `Invoke-WebRequest`.
- **User-facing text:** direct, no hedging, no emoji unless it's a status indicator (🟢🟡🔴). Error messages tell the user what to do next, not just what went wrong.
- **No post-processing / auto-correction features.** John has disabled `PostProcessBatch` and wants it to stay that way — tooling around it is fine but don't enable it by default anywhere.

## Verification checklist

Before declaring done, verify each item below works end-to-end. Document any that don't work with a specific reason and a follow-up issue.

- [ ] `dotnet build -c Release` produces `WhisperInk.exe` with a proper icon visible in File Explorer.
- [ ] `.\scripts\install.ps1` produces a Start Menu entry that launches the app.
- [ ] Desktop shortcut created and works.
- [ ] `About…` dialog shows correct version, git commit hash, build date.
- [ ] Tray icon appears on launch; right-click menu shows all listed items.
- [ ] "Open debug log" opens `%APPDATA%\.WhisperInk\debug.log` in Notepad (or default text editor).
- [ ] "Open config folder" opens Explorer at `%APPDATA%\.WhisperInk\`.
- [ ] Health indicator shows green for a provider with its model files present.
- [ ] Health indicator shows red for a deliberately broken config (e.g. rename the GGUF temporarily).
- [ ] "Copy support bundle" produces a zip with the expected contents, API keys redacted, no GGUFs included.
- [ ] "Diagnose active provider" prints the diagnostic block in a readable format.
- [ ] Main window close = minimize to tray; tray "Quit" actually exits.
- [ ] Auto-start checkbox (if implemented) persists and the registry entry is added/removed correctly.
- [ ] Existing Ctrl+Space dictation flow still works unchanged with the Cohere Local Q4 provider.
- [ ] `%APPDATA%\.WhisperInk\debug.log` still shows the "Batch pipeline" timing line after a dictation.

## Final step

Commit and push. Run `.\scripts\install.ps1 -Desktop` yourself to confirm the full install flow works from a clean user's perspective. Report back with:

1. Final commit hash on `main`.
2. Any deliverables you skipped, and why.
3. Any surprises or gotchas worth documenting in `LEARNINGS.md` (create it if it doesn't exist).
