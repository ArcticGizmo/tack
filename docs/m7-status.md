# M7 status - the off switch and a doctor that actually fixes

> **Done: 2026-09-24.** New CLI commands: `tack disable` / `tack enable`, and `tack doctor --fix`.

Two ergonomics gaps closed. There was no easy way to say "tack, stop / start" (which made dev testing and
"is it tack's fault?" bisecting tedious), and `tack doctor` could *name* a PATH problem but not fix the one
that matters most: a user-PATH shims entry losing to a system-wide install.

## `tack disable` / `tack enable`

A one-word off switch that does **not** touch PATH.

- **Disable** renames the shims dir (`shims` -> `shims_disabled`). The PATH entry still points at `...\shims`,
  which now resolves to nothing, so every tool falls straight through to the real PATH. tack is invisible.
- **Enable** renames it back.

Why rename beats toggling PATH: no env write, no `WM_SETTINGCHANGE` broadcast, no admin, and it's instant -
even already-open shells stop hitting the shims on their next command, because executable resolution checks
the filesystem live. It's also the safest thing to do on an EDR-sensitive box: no interactive PATH mutation.

**Configuration still works while disabled.** The key design point (per the request): disabling turns off
*evaluation*, not *configuration*. `ShimGate.ActiveDir(...)` returns the parked dir while disabled, so
`register` / `bind` / `reshim` stamp shims into `shims_disabled`. Whatever you set up meanwhile goes live the
moment you `tack enable` - one rename, no reshim needed.

Edge handling in `ShimGate.Enable`: if an installer/update recreated an empty `shims` dir while disabled, it's
reclaimed; if that dir is *non-empty*, enable refuses (`Conflict`) rather than clobber it.

Disabled state is profile-scoped, so in a dev build it parks the dev shims dir - which is exactly where the
"make dev testing easier" value lands.

## `tack doctor --fix`

Stops reading the diagnosis and repairs it:

1. **Reshim** - regenerate shims and prune stale ones (into the active dir, so `--fix` works while disabled too).
2. **Promote PATH** - put the shims dir at the **front of the system (machine) PATH**, and strip a now-redundant
   copy from the user PATH.

The machine PATH is the point. On Windows the effective PATH is *machine entries, then user entries*, so a
shims dir on the user PATH still sits behind every system-wide install (a standalone Node, nvm-windows, ...).
Only the front of the **machine** PATH gives true top precedence - and that's an admin-only write.

Elevation is handled with a targeted UAC prompt: the app manifest stays `asInvoker` (never auto-elevates), and
when `--fix` needs the machine PATH it relaunches just that one step (`tack apply-machine-path`, a hidden
command) elevated via the `runas` verb. Decline the prompt and nothing changes; the rest of `--fix` still ran.

In a **dev** profile `--fix` reshims but skips PATH promotion (dev shims are intentionally off PATH).

> This reverses the M4 note that kept PATH mutation at install time to avoid interactive EDR-sensitive writes.
> That was a deliberate call; `doctor --fix` is an opt-in, explicit, single elevated write - the trade-off was
> accepted for this command.

## Code map

| Piece | Where |
| --- | --- |
| Enable/disable + active-dir logic | `Tack.Core.Maintenance.ShimGate` |
| Parked-dir path | `TackPaths.DisabledShimsDir` (`...\shims_disabled`) |
| Machine-PATH arithmetic (pure) | `Tack.Core.Platform.MachinePathPlanner` |
| The admin-gated PATH write + broadcast | `WindowsPathInstaller.PromoteToMachineFront()` |
| Admin check + UAC relaunch | `Tack.Cli.Elevation` |
| Commands | `DisableCommand`, `EnableCommand`, `DoctorCommand --fix`, hidden `ApplyMachinePathCommand` |
| Disabled-aware reporting | `PathDoctor.Run(..., activeShimsDir, disabled)`, `ShimsCommand`, `RegisterCommand` |

## Tests

`ShimGateTests` (park/restore, idempotency, stray-empty reclaim, non-empty conflict, active-dir selection),
`MachinePathPlannerTests` (prepend, move-to-front, no-op when already leading, case/trailing-slash matching),
`PathDoctorDisabledTests` (reports disabled, skips the on-PATH failure). **9 new tests; 67 total, all green.**

## Not verified here

The live machine-PATH promotion and the UAC relaunch aren't exercised in this environment - it would mean a
real elevated write to the system PATH on a managed EDR box. The PATH computation is covered by
`MachinePathPlanner` unit tests; the elevation glue is thin. Worth a manual confirm on a release build.
