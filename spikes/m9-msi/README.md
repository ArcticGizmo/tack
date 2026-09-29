# M9 spike: per-machine Velopack MSI

Throwaway spike for the option-B redesign ([docs/adversarial-review.md](../../docs/adversarial-review.md), finding #1).
Option B puts tack and its shims in an admin-owned Program Files install. This spike checks whether Velopack's
per-machine MSI can support that, using the Velopack and `vpk` 1.2.0 that tack pins.

## Questions

1. Do the install, update and uninstall hooks run **elevated**, and as which account?
2. Can those hooks write the install folder (where admin-owned shims would live) and open the system PATH key for writing?
3. Does `update` into Program Files prompt for UAC, and does the post-update hook then run elevated?

Already answered without installing anything:

- `vpk` 1.2.0 supports `--msi` and `--instLocation PerMachine`.
- `vpk` offers no way to add custom MSI steps, so tack's own hooks would still have to write the system PATH.
- Velopack's MSI template runs the hooks as `Execute="deferred" Impersonate="yes"` custom actions ([velopack#866](https://github.com/velopack/velopack/pull/866/files)). Under UAC, impersonated actions usually get the user's *normal* token unless msiexec was started from an elevated process. This is the main thing to confirm.

## What the spike app does

`tack-msi-spike.exe` registers every Velopack hook. Each hook writes one report to
`%ProgramData%\TackMsiSpike\<time>-<hook>-<pid>.log` recording:

- the account it ran as, whether that's SYSTEM, and whether it was elevated,
- the install folder, and whether it could write a file there,
- whether it could open `HKLM\...\Session Manager\Environment` for writing (opened, never written).

`tack-msi-spike whoami` prints the same report to the console. `tack-msi-spike update <feedDir>` runs the same
update call tack's `update` makes (`WaitExitThenApplyUpdates(silent: true, restart: false)`) against a local feed.

## Build

```powershell
.\build.ps1
```

This produces `msi\TackMsiSpike-1.0.0.msi`, `msi\TackMsiSpike-1.0.1.msi` and the update feed in `releases\`.
It builds only and installs nothing.

## Runbook

Run these from a **normal (non-elevated)** terminal in this folder. Each install or update step should show a UAC prompt.

| Step | Command | Records |
|---|---|---|
| 1. Install 1.0.0 (like double-clicking the MSI) | `msiexec /i msi\TackMsiSpike-1.0.0.msi` | `after-install`: is the install hook elevated when msiexec wasn't? |
| 2. Run normally | `& "$env:ProgramFiles\ArcticGizmo\Tack MSI Spike\tack-msi-spike.exe" whoami` | the baseline token, and `first-run` if Velopack fires it |
| 3. Update to 1.0.1 | `& "$env:ProgramFiles\ArcticGizmo\Tack MSI Spike\tack-msi-spike.exe" update "$PWD\releases"` | whether UAC appears; `before-update` and `after-update` reports |
| 4. Uninstall | Settings → Apps → Tack MSI Spike, or `msiexec /x msi\TackMsiSpike-1.0.1.msi` | `before-uninstall`: elevated or not |
| 5. Install again the way a new `install.ps1` would: `runas` msiexec from a normal terminal | `Start-Process msiexec -Verb RunAs -Wait -ArgumentList '/i', "`"$PWD\msi\TackMsiSpike-1.0.0.msi`"", '/passive'` | `after-install` when msiexec itself is elevated: can the hook write the system PATH and the install folder? |
| 6. Clean up | uninstall again, then delete `%ProgramData%\TackMsiSpike` | |

The install folder is a guess based on Velopack's `Program Files\{publisher}\{packTitle}` rule. Check it after step 1.

## Findings

*(to be filled in from the reports)*
