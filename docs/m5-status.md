# M5 status - distribution hardening

> **Done: 2026-09-23.** Reference: scope plan section 8 + milestone M5.

Most of the distribution chain was front-loaded into **M0** (it had to be, to prove `vpk pack` produced a
working installer). So M5 is not a from-scratch build - it's the hardening pass: closing the one functional
gap in the install lifecycle and confirming the pipeline still holds together.

## What M5 added

**Shim (re)generation on install/update.** The Velopack hook (in `tack-ui`, the mainExe) previously only
wired PATH. Per scope plan section 8 the first-run hook must *also* generate shims for registered tools -
and it matters on two paths a bare PATH-register misses:

- **After an update**, the shim binary itself may have changed (fixes to the proxy). The existing
  `shims\*.exe` are copies of the *old* binary - stale until the user reshims. Now they're re-stamped with
  the new build automatically.
- **After a reinstall** where `config.json` already lists tools, the user gets working shims immediately,
  without having to run `tack reshim`.

Implemented as `Tack.Core.Maintenance.FirstRun.Apply(...)` - PATH first (essential), then a reshim from
existing config with the co-located shim binary. Pure and injectable (`IPathInstaller` + `ShimPayload`), so
it's unit-tested with a fake installer rather than the real registry. The `tack-ui` side (`InstallHook`)
makes the shim step best-effort: a shim copy locked by a running tool mid-update can't fail the install, and
PATH is still wired. Uninstall strips PATH but leaves `%LOCALAPPDATA%\tack` (config + shims) so a reinstall
keeps the registry/bindings the user built up. **2 new tests; 49 total, all green.**

## Distribution chain (state, mostly from M0 - unchanged and still current)

| Piece | State |
| --- | --- |
| `install.ps1` | One-liner (`irm .../install.ps1 \| iex`). Resolves a release, downloads + SHA-256 verifies `Tack-win-Setup.exe` fail-closed, runs it and gets out of the way. Carries the perch lessons: pure ASCII / no BOM, `IWR.Content` byte[] decode, no `Start-Process -Wait` (waits on the Setup handle), `throw` not `exit`. `TACK_VERSION` / `TACK_REPO` pins. |
| `release.yml` | `v*`-tag triggered. Builds all heads + AOT shim on `windows-latest`, records shim latency in the run summary, `vpk pack`s, then a gated `release` job flattens artifacts and generates `SHA256SUMS.txt` over the exact bytes uploaded (fails on a duplicate name or a missing Setup). |
| `publish.bat` | Local pack: publishes the three heads (+ AOT shim, self-contained fallback when the C++ workload is absent), `vpk pack`, writes a local `SHA256SUMS.txt` matching CI's format. |
| `tools/test-install.ps1` | Installer regression suite - asserts the encoded lessons (ASCII/no-BOM/parse across the whole pipeline, hash parsing text+binary+fail-closed, the byte[] decode, asset lookup, the real local manifest). Unchanged by M5 (no pipeline file was touched). |
| PATH wiring | `WindowsPathInstaller` (behind `IPathInstaller`): prepends the shims dir (must win over nvm-windows et al.), appends the install dir, broadcasts `WM_SETTINGCHANGE`. No admin. |

## Deferred (not code - needs an ops/owner decision)

- **Code signing.** Unsigned installs trip SmartScreen and this managed EDR estate. Azure Trusted Signing
  was ruled out (see memory); the path is SignPath Foundation or a purchased OV/EV cert plus a SecOps
  allow-list. This is a signing-identity/ops decision, not code - it can't be closed from the repo.
- **GitHub owner** `ArcticGizmo/tack` is assumed throughout (`install.ps1`, `publish.bat`, metadata),
  matching perch. Confirm before the first public release (scope plan open decision 3).
- **Installer size (~107 MB)** - all heads self-contained single-file. Framework-dependent / trimmed /
  shared-runtime options are cosmetic, deferred.
- **WinGet** follows signing, per the plan's later sequencing.

## Where this leaves v1

M0-M5 complete the v1 scope: the shim (M1), resolution + config (M2), the CLI (M3), the desktop UI (M4),
and a hardened, self-updating distribution story (M5). The remaining items are the plan's explicit "Later"
bucket - `enforce` polish, macOS/Linux heads, global-bin enumeration + auto-reshim (5.5/5.6), version
*installation* backends, central-config sync, and signing + WinGet.
