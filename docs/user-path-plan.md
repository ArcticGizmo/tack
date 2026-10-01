# Plan: shims on the user PATH (ADR 0002)

*Drafted 2026-10-01 on `docs/adversarial-review`. Implements [ADR 0002](adr/0002-shims-on-the-user-path.md) and
replaces the [M9 plan](m9-per-machine-plan.md) from phase 4 onwards.*

tack is still greenfield, so this plan breaks things freely: no migration, no compatibility shims.

## Where things stand

M9 phases 1 to 3 are done: the shim reads only its own account's config, `disable` is a per-user setting, and
compiling config is separate from stamping shims. Releases still ship Velopack's per-user `Setup.exe` (M9 phase 6
never happened), so packaging barely changes. What's left over from M9 is the elevation code: `TackPaths.Machine`,
the UAC relaunch, `tack elevated`, `apply-machine-path`, the first-run prompt, and every write to the system
PATH. That code is what this plan removes.

## Target layout

| What | Where | On PATH |
|---|---|---|
| tack binaries | `%LOCALAPPDATA%\Tack\current\` (Velopack) | user PATH, last |
| shims | `%LOCALAPPDATA%\tack\shims\` | user PATH, **first** |
| config, `resolved.json`, logs, PATH backups | `%LOCALAPPDATA%\tack\` | no |
| dev shims | `%LOCALAPPDATA%\tack (Dev)\shims\` | user PATH, just behind release's shims |
| dev config | `%LOCALAPPDATA%\tack (Dev)\` | no |

`%LOCALAPPDATA%\Tack` (Velopack's root) and `%LOCALAPPDATA%\tack` (tack's data) are the same folder, because
NTFS is case-insensitive. The shims sit in the root rather than in `current\`, so updates keep them. Nothing goes
on the system PATH.

## Decisions taken (overrule any)

| # | Decision | Instead of |
|---|---|---|
| U1 | **The install hook wires the user PATH and stamps the shims itself.** Writing HKCU needs no UAC and takes milliseconds, well inside Velopack's 15 to 30 s callback limit. First-run setup, `TACK_SKIP_FIRSTRUN` and the setup mutex go. | a first-run console window |
| U2 | **`tack setup` stays as the repair command, for both profiles.** Release goes to the front of the user PATH; dev goes just behind release's shims. `doctor --fix` does the same. | dev refusing `setup` |
| U3 | **Stale shims are pruned on every sync.** The shims folder is yours alone now, so nothing else can be relying on a shim your config doesn't list. Only `<name>.exe` for valid names is ever deleted. | M9's D3 (leave them until `doctor --fix`) |
| U4 | **Old system PATH entries are reported, never removed.** `doctor` fails if any tack folder is on the system PATH (0.1.x put the shims there, and M9 dev testing put `Program Files\Tack (Dev)\shims` there), and prints a command to remove them that you run in an admin shell. tack never writes the system PATH, even to clean up after itself. | an elevated clean-up step |
| U5 | **A Windows-owned name is refused.** That's a name found in System32, in the Windows folder, or in a system PATH folder under `%SystemRoot%`. `tool add` rejects it if given explicitly, and skips it with a note if auto-detected. Moving it isn't possible, and `CreateProcess` searches System32 before PATH anyway. | review #5's "require confirmation" |
| U6 | **The shadow check matches cmd:** folder by folder in PATH order, trying each `PATHEXT` extension in a folder before moving on. | `.exe`/`.cmd`/`.bat` only |
| U7 | **`which` stays scriptable.** stdout is still the path tack resolves; if a plain call wouldn't reach tack, a warning goes to stderr. `info` shows it as a line. | changing `which`'s output |

## Phases

Each phase is one commit that builds, passes the tests, and can be reviewed alone. The end-to-end shim tests
(`ShimTests`) start real processes, so **you run them**. I run the rest.

### Phase 1: per-user layout, user PATH, no elevation

- `TackPaths`: drop `Machine`. `User` gains `ShimsDir`, `PathBackupsDir`, `ReleaseShimsDir` and `AllShimsDirs`,
  all under `%LOCALAPPDATA%`. The install dir stays `AppContext.BaseDirectory`.
- `TackProfile.ForShimsDir`: the dev shims' parent is `tack (Dev)` under `%LOCALAPPDATA%`.
- `WindowsEnvRegistry`: `WriteMachine` becomes `WriteUser`. Reading the machine PATH stays.
- `WindowsPathInstaller`: edits the user PATH. Register puts the shims at the front (dev: behind release's shims)
  and appends the install dir (release only); Unregister removes both.
- `SystemPath` becomes `UserPath`: edit in-process, back up the before and after to `path-backups`, report.
- Delete `Elevation`, `ElevatedCommands`, `ApplyMachinePathCommand`, `InstallHook.FirstTimeSetup`/`IsFirstRun`
  and the setup mutex.
- `Shims.Sync` stamps and prunes directly (U3). `Shims.PruneStale` goes.
- `FirstRun.Apply` always registers and stamps (U1). The uninstall hook unregisters.
- `install.ps1`: drop `TACK_SKIP_FIRSTRUN`; `tack setup` becomes a check that prints where tack is on the user
  PATH. Stays pure ASCII.
- Tests: paths, PATH edits, first run.

### Phase 2: the shadow report and Windows-owned names

- Core `CommandLookup`: for a name, the folder a new process would run it from, given the expanded system PATH,
  the user PATH and `PATHEXT` (U6), and whether that folder comes before tack's shims. It's one shared
  implementation, replacing `PathDoctor`'s loop.
- `WindowsCommands.Owns(name)` (U5), from System32, the Windows folder and system PATH folders under `%SystemRoot%`.
- `tool add`: refuse or skip Windows-owned names; after saving, report each exposed name that won't be
  intercepted, saying what wins and what to do.
- `doctor`: shadowed names with the same guidance; **fail** when a tack folder is on the system PATH (U4), with
  the removal command; warn when tack's shims aren't first on the user PATH (`--fix` repairs it).
- `info` / `which` (U7).

### Phase 3: doctor checks who can write tack's folders

- ACL-based, no probe files: `tools/audit-path.ps1`'s rules in C#. The shims folder, `current\` and the data
  folder must not be writable by any account other than you, SYSTEM and Administrators (for example if
  `%LOCALAPPDATA%` is redirected to a share). That's the only route left from another account into your sessions.

### Phase 4: docs

- README: install, PATH (user PATH, and what "shadowed" means), uninstall, the scope ("your processes").
- CHANGELOG `[Unreleased]`.
- Review doc: #1 fixed, and #3, #4 and the elevated half of #9 gone with the elevated code. M9 plan: point here.

### Phase 5: manual end-to-end check (you run it)

From a local `publish.bat` build:

1. Remove leftover tack entries from the system PATH (doctor prints the command).
2. Install through `install.ps1` pointed at the local build. No UAC prompt should appear at any point.
3. `doctor` is clean.
4. `tool add` a tool; run it from a normal shell, an elevated shell, an IDE started from the Start menu, and a
   scheduled task that runs as you.
5. Run it as a second account: tack shouldn't be involved.
6. `tool add` something the system PATH already has: it's reported as shadowed.
7. `update` to a second local build, then uninstall: the user PATH entries go.
