# LEARNINGS

Gotchas and non-obvious bits discovered during the packaging-polish sprint. Keep this additive and short — the point is to spare the next person the same dead-end.

## UseWindowsForms in a WPF project

Flipping on `<UseWindowsForms>true</UseWindowsForms>` alongside `<UseWPF>true</UseWPF>` adds *implicit global usings* for both `System.Windows.Forms` and `System.Drawing`. That instantly breaks every existing file that referenced `Application`, `MessageBox`, `Clipboard`, `Button`, or `Color` unqualified — the WPF versions collide with the WinForms/GDI+ versions.

Two fixes are needed in the csproj:

```xml
<ItemGroup>
  <Using Remove="System.Windows.Forms" />
  <Using Remove="System.Drawing" />
</ItemGroup>
```

(Plus a `Target BeforeTargets="BeforeCompile"` that does the same thing, because `wpftmp` csproj files the WPF build generates don't inherit the ItemGroup.)

Tray code references the WinForms types with fully qualified names (`System.Windows.Forms.NotifyIcon`, `System.Drawing.Icon`).

## SourceRevisionId beats hand-rolling InformationalVersion

The .NET SDK already appends `+<SourceRevisionId>` to `AssemblyInformationalVersion` when it's populated. Setting `InformationalVersion` directly creates **double** appends: `1.0.0+30f9fde.<full-hash>`. Instead, write the short hash to `SourceRevisionId` via a pre-build `Target` and let the SDK do the concatenation — yields a clean `1.0.0+30f9fde`.

## Single-file publish zeroes Assembly.Location

`Assembly.GetExecutingAssembly().Location` returns an empty string in single-file publish mode, which surfaces as IL3000 warnings. Read the running exe path via `Process.GetCurrentProcess().MainModule.FileName` (with an `AppContext.BaseDirectory` fallback) instead.

## The old publish scripts never worked from the repo root

`publish.ps1` / `publish-framework-dependent.ps1` used to do:

```powershell
$ProjectFile = Join-Path $SolutionDir $ProjectName "$ProjectName.csproj"
```

On Windows PowerShell 5.1 that's a 3-arg `Join-Path` call which errors; on PowerShell 7 it builds `<root>\WhisperInk\WhisperInk.csproj`, but the csproj lives at the repo root, so the file check fails immediately. Both scripts now use a direct `$repoRoot\WhisperInk.csproj` path.

## MainWindowHandle = 0 is expected

The floating status bar is `WindowStyle="None"` + `ShowInTaskbar="False"`. PowerShell's `Get-Process | Select MainWindowHandle` reports `0` for WhisperInk processes even when they're healthy and visible — not a crash. Check with `Get-Process WhisperInk` count + `debug.log` contents instead.

## The hook needs the window alive — so "close" must hide

`SetWindowsHookEx` is installed on the main window's UI thread (`MainWindow_Loaded`). Closing the window tears the hook down, so the tray-based "minimize to tray" pattern is implemented by intercepting `Closing` and calling `Hide()` instead of actually closing. `Application.Shutdown()` from the tray's Quit item sets `_exiting = true` so the Closing handler lets it through.

## Commit shape vs. deliverable shape

The original packaging-polish prompt asks for one focused commit per deliverable. In practice deliverables 3–8 (tray, health probe, support bundle, diagnose, first-run, auto-start) all land in `MainWindow.xaml.cs` through the same `TrayIconHost` interface and can't be cleanly split without `git add -p`. They went in as one `tray:`-prefixed commit with a body that enumerates each deliverable. If the split matters for review, the commit body is the roadmap.
