# M4 status - the desktop UI

> **Done: 2026-09-23.** The Avalonia `tack-ui` head, fleshed out from the M0 placeholder into a first-class
> inspector + editor over the M2/M3 engine. Reference: scope plan section 7 + milestone M4.

## Shape

A tabbed window (`TabControl`, left rail) with one screen per concern, MVVM throughout. Every screen is a
thin shell over `Tack.Core` via `TackServices` - the UI counterpart of the CLI's `TackEnvironment` (Core
decides, the UI presents). No resolution or maintenance logic lives in the front-end.

- **Composition root** in `App.axaml.cs`: builds the window, wires `TackServices` (Core) + a `DialogService`
  (folder picker over the window's `StorageProvider`) into a `MainWindowViewModel`.
- **`MainWindowViewModel`** owns one view model per tab and re-reads that tab's data when it's selected, so a
  change on one tab (register, bind, reshim) shows the moment you switch to a tab that depends on it - no
  cross-view-model coupling.
- **No new dependencies.** A tiny hand-rolled `ViewModelBase` (INotifyPropertyChanged) + `RelayCommand` /
  `AsyncRelayCommand` instead of an MVVM toolkit. Mutating actions run off the UI thread (`Task.Run`) and
  disable their command while in flight.
- **Compiled bindings** (`AvaloniaUseCompiledBindingsByDefault`) with `x:DataType` on every view/template, so
  every binding path is validated at build time.
- **Nord (Dark) theme** via the `ArcticGizmo.Avalonia.Palette` package perch themes from. `ThemeManager`
  publishes the palette's ~70 semantic token brushes into `Application.Resources`; views paint with
  `{DynamicResource PanelBgBrush}` / `FgBrush` / `AccentBrush` / `Ok`/`Warn`/`DangerBrush`, and Fluent's own
  `SystemAccentColor` is pointed at the Nord frost accent so tab selection/focus are Nord too (not the
  Windows system accent). Surfaces render Nord Polar Night `#3B4252`.

## Screens

| Tab | Does | Core it drives |
| --- | --- | --- |
| **Dashboard** | Summary (tools/versions/bindings/defaults + shims-on-PATH) and how every tool resolves for a chosen directory (defaults to cwd). | `ResolveAll`, `Doctor` |
| **Inspector** | Pick any folder; per-tool resolution + a detail panel (version, source, why, binary). The "why is that IDE using the wrong node?" debugger. | `ResolveAll` |
| **Registry** | Register installs (tool@version -> binDir + exposes, with auto-detect), set default, remove. Saves + reshims. | `ToolProbe`, `ConfigStore`, `Reshimmer` |
| **Bindings** | Central glob -> tool@version rules, with an `enforce` flag; add / remove. Saves + reshims. | `ConfigStore`, `Reshimmer` |
| **PATH** | PathDoctor checks + the effective PATH visualised, shims dir highlighted and shadowers flagged. Read-only. | `PathDoctor` |
| **Shims** | Generated shims, stale ones flagged, one-click reshim. | `Reshimmer` |

## Notes / follow-ups

- **Source of truth is the real config.** The editors write `%LOCALAPPDATA%\tack\config.json` and reshim,
  exactly as the CLI commands do - the UI and CLI are interchangeable front-ends.
- **Dev-run shim stamping** carries the M3 caveat: under a `dotnet run`, `tack-shim.exe` isn't beside
  `tack-ui`, so reshim writes `resolved.json` but stamps no copies. The UI reports that plainly. In a
  Velopack install they're co-located and shims generate.
- **PATH stays read-only in the UI**, matching the M3 decision: PATH mutation lives in the install-time
  Velopack callback (`WindowsPathInstaller`), not in an interactive EDR-sensitive write. A future
  `doctor --fix` / UI repair could add it.
- **No UI unit tests.** The screens are thin over already-tested Core; compiled bindings give build-time
  binding validation, and a launch smoke-test confirms the window initializes. The 47 Core tests are
  unchanged and green.

## Running locally (dev profile)

A dev build must not clobber an installed release's data, so `TackProfile` (Tack.Core) routes a **Debug**
build to an isolated `%LOCALAPPDATA%\tack (Dev)` data space (its own config.json, resolved.json and shims);
a release install uses `%LOCALAPPDATA%\tack`. Force either way with `TACK_DEV=1` / `TACK_DEV=0`. The window
title and `tack doctor` show the dev profile so it's obvious which space you're in.

- **`run-ui.bat`** launches the UI from source (`dotnet run --project src/Tack.App`).
- **`run.bat open`** also works now: `tack open` locates the sibling `Tack.App` build for the same
  configuration when tack-ui isn't co-located (the dev layout), instead of failing with "tack-ui not found".
- In dev the shims dir genuinely isn't on PATH (install-time wiring is release-only), so `tack doctor` will
  flag that - expected, and it says so.

## Next: M5 - distribution hardening

`install.ps1` + `SHA256SUMS.txt` + `release.yml`, the ported `test-install.ps1` suite, and first-run
PATH/shim wiring on install - porting perch's distribution story with its ASCII / `byte[]` / no-`-Wait`
lessons.
