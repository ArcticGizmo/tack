# M9 spike: per-machine Velopack MSI

> **Done; don't re-run the runbook below.** This spike's registry probe was flagged by CrowdStrike Falcon.
> The results are under [Findings](#findings-2026-09-29). They were completed with a blander local probe
> (`sandbox/uac-probe`, gitignored) whose installs the user ran by hand.

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

Run these from a **normal (non-elevated)** terminal in this folder. `$app` is the installed exe; its folder is a
guess from Velopack's `Program Files\{publisher}\{packTitle}` rule, so check it after the first MSI install.

```powershell
$app = "$env:ProgramFiles\ArcticGizmo\Tack MSI Spike\current\tack-msi-spike.exe"
```

**Part 1: moving an existing per-user install over, and the double-click install**

| Step | Command | Answers |
|---|---|---|
| 1. Install today's way: per-user `Setup.exe`, no UAC | `installers\TackMsiSpike-1.0.0-Setup.exe` | baseline: the per-user folder and uninstall entry |
| 2. Install the MSI like a double-click (msiexec not elevated) | `Start-Process msiexec -Wait -ArgumentList '/i', "`"$PWD\installers\TackMsiSpike-1.0.0.msi`"", '/passive'` | does it clash with the per-user install? Is the hook elevated when msiexec wasn't? |
| 3. Run normally | `& $app whoami` | **can a normal process write the install folder?** (must be `no`) |
| 4. Uninstall both | `Start-Process msiexec -Wait -ArgumentList '/x', "`"$PWD\installers\TackMsiSpike-1.0.0.msi`"", '/passive'`, then the per-user one from Settings → Apps | is the uninstall hook elevated? |

**Part 2: the `install.ps1` route and updates**

| Step | Command | Answers |
|---|---|---|
| 5. Install the way a new `install.ps1` would: `runas` msiexec | `Start-Process msiexec -Verb RunAs -Wait -ArgumentList '/i', "`"$PWD\installers\TackMsiSpike-1.0.0.msi`"", '/passive'` | is the hook elevated, can it write the install folder and open the PATH key? It also writes marker files in the install root and in `current\` |
| 6. Update to 1.0.1 | `& $app update "$PWD\releases"` | one UAC prompt? `before-update` and `after-update` elevated? |
| 7. What survived the update | `& $app check` | whether the root and `current\` markers survived |
| 8. Clean up | uninstall, then delete `%ProgramData%\TackMsiSpike` | |

Hook reports land in `%ProgramData%\TackMsiSpike`. Installed apps are listed under
`HKCU:\...\Uninstall` (per-user) and `HKLM:\...\Uninstall` (per-machine).

## Findings (2026-09-29)

This spike's first run (Velopack 1.2.0) was flagged by CrowdStrike Falcon partway through, most likely because
each hook opened the system PATH key for write. The remaining questions were answered with a blander probe
(`sandbox/uac-probe`, gitignored, Velopack 1.2.158, no registry access). The user ran every install, update
and uninstall by hand.

### Velopack 1.2.0 (the version tack pinned)

| Check | Result |
|---|---|
| `/passive` per-machine MSI install location | **`C:\<packTitle>\`**, not Program Files: the step that picks Program Files (`SetINSTALLFOLDER`) is skipped. The folder inherits **Authenticated Users: Modify** from `C:\`, so any user can write it. Fixed in 1.2.158. |
| Hooks when msiexec is **not** elevated (double-click route) | install and uninstall hooks ran as the user, **not elevated**, and couldn't open the PATH key for write |
| Per-user `Setup.exe` install plus the MSI | installed side by side with separate uninstall entries (HKCU `TackMsiSpike`; HKLM `MSI:TackMsiSpike` and the product GUID). Nothing migrates automatically. |

### Velopack 1.2.158

| Check | Result |
|---|---|
| `vpk pack` without `--runtime` | defaults to **x86**, i.e. Program Files (x86). Pass `--runtime win-x64`. |
| Install location | `C:\Program Files\<packId>\` (the docs say `Program Files\{publisher}\{packTitle}`). The root holds `Update.exe`, a `<packTitle>.exe` launcher that opens its own window, `.msi-installed` and `packages\`; the app itself is in `current\`. |
| Permissions after install | root and `current\` owned by SYSTEM; Users have read and execute only. A normal process **can't** write the install folder. |
| Install via `runas` msiexec (the new `install.ps1` route) | `after-install` ran **elevated, as the installing user**, and could write both the root and `current\` |
| Asking for UAC from the installed app | works |
| Update run from a **normal** process | the UAC prompt appears and `after-update` is elevated, **but** the package is staged in the user's `%LOCALAPPDATA%\<packId>\packages\`, the elevated step runs a copy of `Update.exe` from `%LOCALAPPDATA%\<packId>\`, and afterwards `current\` is **writable by the normal user**, probably because a folder built in the profile keeps its permissions when moved in. **Unsafe; tack must never do this.** |
| Update run from an **elevated** process | staged in `C:\Program Files\<packId>\packages\`; `current\` is then owned by Administrators with standard Program Files permissions, so a normal process **can't** write it; `after-update` ran elevated. **Safe.** |
| Files that survive an update | a file in the install **root** survives; `current\` is replaced. So shims can live in the root (e.g. `C:\Program Files\Tack\shims`). |
| Normal launch of the installed app | copies `Update.exe` into `%LOCALAPPDATA%\<packId>\` and creates an empty `packages\` there (seen at the exact time of a normal `whoami`). Harmless unless an unelevated update uses it. It isn't removed on uninstall. |
| Uninstall from Settings → Apps | `before-uninstall` ran **elevated**, as the user |
| Version given to the uninstall hook after an update | the MSI's **original** version (1.0.0 after updating to 1.0.1). Don't rely on it. |

Not retested on 1.2.158: the double-click (non-elevated msiexec) route.

### What this means for tack under option B

- **Install:** `install.ps1` downloads and checks the MSI (keeping the file open until msiexec exits), then runs `msiexec /i … /passive` with `runas`. The install hook is elevated, so it writes the system PATH and stamps the shims itself. `tack setup`, the first-run prompt, `TACK_SKIP_FIRSTRUN` and the setup mutex can go. The double-click route still needs a fallback, because its hooks may not be elevated.
- **Layout:** shims in `C:\Program Files\Tack\shims` (in the root, so updates keep them, and admin-only), first on the system PATH; `C:\Program Files\Tack\current` last.
- **Update:** `tack update` must relaunch itself elevated and do the whole check, download and apply in the elevated process. It must never apply an update from an unelevated process.
- **Uninstall:** the elevated `before-uninstall` hook removes tack's PATH entries. Ignore the version it's given.
- **Migration:** the MSI doesn't remove an existing per-user install, so tack has to remove the old install and its `%LOCALAPPDATA%` PATH entries itself.
- **Packaging:** pin Velopack and `vpk` to 1.2.158 together, pack with `--msi --instLocation PerMachine --runtime win-x64`.
