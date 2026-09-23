# M0 status - skeleton + Velopack pack

> **Done: 2026-09-23.** M0 stands up the real solution, graduates the proven M1 shim into it, and proves
> the full distribution pack. Reference: `docs/scope-and-implementation-plan.md` milestone M0 (scoped to
> "also prove Velopack pack").

## What's in place

| Area | State |
| --- | --- |
| Solution `tack.slnx` + 5 projects | `Tack.Core`, `Tack.Cli` (`tack.exe`), `Tack.Shim` (`tack-shim.exe`, NativeAOT), `Tack.App` (`tack-ui.exe`, Avalonia), `tests/Tack.Tests` (+ `tests/Tack.StubTool`). Builds clean. |
| Graduated shim | `Tack.Shim` + tests moved from `spikes/m1-shim` into `src/` and `tests/`, carrying the `.cmd`-via-`cmd.exe` fix. 7/7 tests pass. |
| `tack --version` | Prints `tack 0.1.0` (version from `Directory.Build.props`). `tack open` launches `tack-ui`. |
| Binary metadata (your note) | `Directory.Build.props` sets Company/Product/Description/Version + Copyright on every exe; each head has an `app.manifest` (asInvoker, longPathAware, UTF-8, Win10 supportedOS). A copied `node.exe` now identifies honestly as tack. A deliberate deviation from perch (which sets almost none). |
| PATH install | `WindowsPathInstaller` (in `Tack.Core`, behind `IPathInstaller`) prepends the shims dir + appends the install dir to the user PATH and broadcasts `WM_SETTINGCHANGE`. Wired to Velopack install/update/uninstall callbacks in `tack-ui`. |
| Velopack pack | `publish.bat` publishes all three heads + AOT shim (fallback to self-contained locally) and `vpk pack`s `Tack-win-Setup.exe` + update feed + `SHA256SUMS.txt`. Proven locally. |
| `install.ps1` | Ported from perch (ASCII/no-BOM, `byte[]` decode, no `Start-Process -Wait`, throw-not-exit). `TACK_VERSION` / `TACK_REPO`. |
| `release.yml` | Windows-only (v1). Publishes AOT shim on `windows-latest` (has the C++ workload), runs the latency bench into the run summary, packs, then a gated release job flattens artifacts + generates `SHA256SUMS.txt`. |
| `tools/test-install.ps1` | Installer regression suite. 27/27 checks pass (encoding/parse, hash parsing, `byte[]` decode, asset lookup, real manifest). |
| `tools/bench-shim.ps1` | The M1 latency bench, now driven by CI against the AOT build. |
| `.editorconfig` | Mirrors perch's encoding policy (source = UTF-8 BOM; pipeline scripts = ASCII/no-BOM; `.sh` = LF). |

## How to build / test / pack

```powershell
dotnet build tack.slnx -c Release
dotnet test  tests\Tack.Tests\Tack.Tests.csproj -c Release
.\run.bat --version
powershell -ExecutionPolicy Bypass -File tools\test-install.ps1
.\publish.bat            # -> releases\Tack-win-Setup.exe + SHA256SUMS.txt
```

## Known follow-ups (not blockers)

- **Installer size ~107 MB.** All three heads are self-contained single-file, so each bundles a full
  runtime. Options for later: framework-dependent publish, trimming, or a single shared runtime. Cosmetic
  for now.
- **AOT latency number** still comes from CI (no C++ workload locally). `release.yml` records it in the run
  summary on the first tagged build.
- **GitHub owner** (`ArcticGizmo/tack`) is assumed in `install.ps1` / metadata - confirm before releasing
  (scope plan open decision 3).
- **Signing** deferred to M5 but pulled forward in priority for the managed EDR estate (Azure Trusted
  Signing ruled out; SignPath Foundation or a purchased OV/EV cert + a SecOps allow-list).
- **The `spikes/m1-shim/` tree is a frozen record** (the shim now lives in `src/`); its `reach-test/`
  acceptance harness still works against its own copied build.

## Next: M2 - resolution + config

The `Tack.Core` resolver with full precedence (env override -> tack.yml walk-up -> central binding ->
default -> passthrough), `config.json` (registry + bindings + defaults) compiled to `resolved.json`, and
the "both parsers agree" cross-check test. That's where the shim stops being hard-wired and tack becomes
useful.
