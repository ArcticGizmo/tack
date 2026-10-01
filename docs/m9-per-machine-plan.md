# M9 plan: per-machine install (option B)

*Drafted 2026-09-29 on `docs/adversarial-review`. Fixes review finding #1, and with it most of #3, #4 and #9.*

> **Superseded from phase 4 onwards (2026-10-01)** by [ADR 0002](adr/0002-shims-on-the-user-path.md): shims go on
> the user PATH and nothing goes on the system PATH. Phases 1 to 3 stand, with stamping now unelevated. Phases 4
> to 6 and decisions D2, D3, D5, D7, D8 and D9 are dropped. The replacement is
> [docs/user-path-plan.md](user-path-plan.md).

tack is still greenfield, so this plan **breaks things freely**. There's no migration from the per-user install, no
compatibility with old config layouts, and no second install route kept alive "just in case". The evidence for
each design choice is in the [review](adversarial-review.md) (the #1 decision) and the
[M9 spike findings](../spikes/m9-msi/README.md#findings-2026-09-29).

## Goal

Nothing on the system PATH can be written by a normal user, and a shim only ever trusts the config of the account
it's running as. In practice:

- SYSTEM, services and other users get plain passthrough;
- your own shells, elevated or not, get your pinned versions;
- a malicious program running as you gains nothing beyond what it already had.

## Target layout

| What | Where | Who can write it |
|---|---|---|
| tack binaries (`tack.exe`, `tack-shim.exe`, Velopack runtime) | `C:\Program Files\Tack\current\` | admins |
| Velopack's `Update.exe`, `packages\` | `C:\Program Files\Tack\` | admins |
| **shims** | `C:\Program Files\Tack\shims\` (in the root, so updates keep them) | admins |
| machine PATH backups | `C:\Program Files\Tack\path-backups\` | admins (users can read) |
| your config, `resolved.json`, invocation log | `%LOCALAPPDATA%\tack\` | you |
| dev profile shims | `C:\Program Files\Tack (Dev)\shims\` | admins |
| dev profile config | `%LOCALAPPDATA%\tack (Dev)\` | you |

System PATH: `C:\Program Files\Tack\shims` **first**, `C:\Program Files\Tack\current` **last**. Nothing under
`%LOCALAPPDATA%` goes on any PATH.

The folder name comes from Velopack's pack ID (`Tack`). The spike found the MSI installs to
`Program Files\<packId>`, not `{publisher}\{packTitle}` as documented.

## Trust rules the code must keep

1. **The shim reads only the calling account's config.** It looks in `TACK_RESOLVED`, else
   `%LOCALAPPDATA%\<profile>\resolved.json` for the token's own `%LOCALAPPDATA%` (from the known-folder API).
   The profile (`tack` or `tack (Dev)`) comes from the name of the folder the shims live in. That folder is
   admin-owned, so a normal user can't change which profile a shim uses.
   - `TACK_RESOLVED` stays because it's the test hook, and anything that can set your environment can already set your PATH.
   - The "next to the shim" and "folder above the shims folder" lookups are removed.
2. **No config means passthrough, not an error.** That's the SYSTEM, service and other-user case, and tack has to be invisible there.
3. **Elevated code never writes where a normal user can.** Every elevated step works out its paths from its own
   location in Program Files. It takes no folder arguments. The only input it takes is shim names, and those are checked
   against `^[A-Za-z0-9][A-Za-z0-9._+-]*$`.
4. **Shim binaries are only ever copied from `C:\Program Files\Tack\current\`,** never from a user-writable folder.
   The dev profile is the exception: its shims come from the repo's build output, which is fine for a developer on
   their own machine.
5. **Updates are only applied from an elevated process.** Applying from an unelevated one stages the package in
   `%LOCALAPPDATA%`, runs an `Update.exe` copy from there, and leaves `current\` user-writable (spike round 1).

## Decisions taken (overrule any)

| # | Decision | Instead of |
|---|---|---|
| D1 | **`tack disable` / `enable` become a per-user setting** (`settings.disabled` in config, compiled into `resolved.json`); the shim passes through straight away. No admin, and it only affects you. | renaming the shims folder, which now needs admin and would switch tack off for everyone |
| D2 | **Adding a new command name needs one UAC prompt** (an elevated step stamps the new shim). New versions, zones and settings never do. | per-user shims, which can't be on the system PATH safely |
| D3 | **Stale shims stay until `tack doctor --fix`.** A shim for a name nobody configures just passes through, so leaving it is harmless. Removing shims automatically would mean knowing every user's config. | pruning on every reshim |
| D4 | **No migration.** `install.ps1` refuses to run if the old per-user install (`%LOCALAPPDATA%\Tack\Update.exe`) is present, and says how to remove it. Back up `config.json` first, because the old uninstaller may delete that folder. | detecting and converting old installs |
| D5 | **Only the MSI and the update feed are published.** `Setup.exe` (per-user) and the portable zip are dropped from releases. | keeping a per-user route that brings the whole problem back |
| D6 | **`tack setup` stays, as the repair command:** it wires PATH and shims through UAC. First-run setup, `TACK_SKIP_FIRSTRUN` and the setup mutex go. `install.ps1` doesn't need `tack setup`, because the install hook does it. | the first-run console window |
| D7 | **Hidden elevated commands are grouped under `tack elevated <op>`:** `wire`, `unwire`, `shims <names…>`, `prune`, `update`. Each works out its own paths. `apply-machine-path` and its folder arguments are removed. | patching `apply-machine-path` with validation |
| D8 | **The dev profile stays on PATH behind release,** in `Program Files\Tack (Dev)\shims`, wired by an elevated dev `doctor --fix`. | dropping the dev-on-PATH workflow |
| D9 | **Accept that Velopack keeps a stray `Update.exe` and `packages\` in `%LOCALAPPDATA%\Tack\`,** which is the same folder as tack's data. tack never runs it (rule 5), and `doctor` warns if it's ever newer than the Program Files copy. | renaming the data folder |

## Phases

Each phase is one commit that builds, passes the tests, and can be reviewed alone. The end-to-end shim tests start
real processes, so **you run them**. I run the pure tests.

### Phase 1: the shim reads only the caller's config

- `Tack.Shim/Program.cs` `FindResolved`: `TACK_RESOLVED`, then the profile's `resolved.json` under the token's
  `%LOCALAPPDATA%`. Drop the next-to-shim and folder-above-shims lookups.
- New `TackProfile.ForShimsDir(path)`: `tack (Dev)` if the shims folder's parent is named `Tack (Dev)`, else `tack`.
- A missing `resolved.json` means passthrough. An unreadable or corrupt one still fails loudly: that's your own config, and hiding the problem would be worse.
- Tests: missing config passes through; the profile follows the folder name; `TACK_RESOLVED` still wins.

### Phase 2: machine and user paths; disable as a setting

- `TackPaths`: split into `Machine` (install root, shims, path-backups; found from `AppContext.BaseDirectory` for
  release, `%ProgramFiles%\Tack (Dev)` for dev) and `User` (config, `resolved.json`, logs).
- `settings.disabled` in `CentralConfig` and `TackSettings`. The shim checks it right after loading config.
- `tack disable` / `enable` just flip the setting and recompile `resolved.json`. Delete `ShimGate`,
  `DisabledShimsDir` and `ActiveShimsDir`, along with their tests.
- `PathFixBackup` writes to the machine `path-backups` folder (elevated side) and the parent reads it from there.

### Phase 3: compiling and stamping are separate

- `Reshimmer` is split in two:
  - **Compile** (`config.json` → `resolved.json`): unelevated, and runs on every mutation.
  - **Stamp** (write shim copies into `Program Files\Tack\shims`): elevated only.
- After a compile, the CLI compares the user's exposed names with the shims folder. Missing names or an out-of-date payload
  trigger **one** `tack elevated shims <names…>` UAC prompt. If you decline: "configured, but `npm` isn't
  intercepted until you run `tack setup`".
- Validate `exposes` names (rule 3) in `tool add`, and when loading config. Bad names are rejected with a message.
- The "unchanged" check compares a SHA-256 hash instead of size and timestamp (review: code quality).
- Only known shim names are ever deleted, and only by `tack elevated prune` (`doctor --fix`).

### Phase 4: elevated commands and the Velopack hooks

- `tack elevated wire|unwire|shims|prune|update`: hidden, and they refuse to run unelevated or outside Program Files.
- Install hook, elevated in the `install.ps1` route: wire the system PATH, create `shims\`, and stamp the installing user's names.
- Update hook, elevated because updates always are: restamp **every** shim already in `shims\` with the new
  payload (other users' names included), and re-check PATH.
- Uninstall hook, elevated from Settings: unwire PATH. Velopack removes the folder. Ignore the version it's given,
  because it stays at the MSI's original version.
- If a hook runs unelevated (the double-click route), it does nothing, and `tack doctor` says "run `tack setup`".
- Delete `InstallHook.FirstTimeSetup`, `IsFirstRun`, `TACK_SKIP_FIRSTRUN`, the `Local\tack-path-setup` mutex and
  `ApplyMachinePathCommand`.
- `Elevation.RelaunchElevated` now relaunches a Program Files `tack.exe`, which closes the "swap `tack.exe` before
  the next UAC prompt" part of #4.

### Phase 5: updates are elevated

- `tack update --check`: unelevated, and only reads the feed.
- `tack update`: if not elevated, relaunch as `tack elevated update` through UAC. That process checks, downloads
  and applies. It never calls `DownloadUpdatesAsync` or `WaitExitThenApplyUpdates` unelevated.
- The elevated child's console waits for Enter so its output can be read, reusing `OwnsConsoleWindow`.
- `TACK_REPO` stops being read from the environment; add a `--repo` flag instead (review #6).

### Phase 6: packaging and `install.ps1`

- `release.yml` / `publish.bat`: `vpk pack --msi --instLocation PerMachine --runtime win-x64`. Publish the MSI,
  the update feed (`*.nupkg`, `releases.win.json`, `RELEASES`, `assets.win.json`) and `SHA256SUMS.txt`. Leave out `Setup.exe` and
  the portable zip. The "installer is missing" check now looks for `Tack-win.msi`.
- `install.ps1`:
  - refuse if the old per-user install is present (D4);
  - download and check `Tack-win.msi`, **keeping it open with read-only sharing until msiexec exits**, so it can't be
    swapped between the hash check and the elevated install;
  - run `msiexec /i … /passive /norestart /l*v <log>` with `runas`;
  - map exit codes: 0 ok, 3010 ok with reboot, 1602 or 1223 cancelled, anything else failed (point at the log).
  - Stays pure ASCII.
- `tools/test-install.ps1`: new asset names. It loads `install.ps1` with `Invoke-Expression`, which EDR
  dislikes; switch to dot-sourcing a filtered copy written next to it in the repo.
- Pin actions to SHAs and give `contents: write` to the release job only (review #10), while touching the file anyway.

### Phase 7: doctor, README, review doc

- New `tack doctor` checks:
  - the shims folder and `current\` **aren't writable by you** (the same write test the probe used; it catches a
    repeat of the unelevated-update problem);
  - PATH order;
  - shims present for your names;
  - the stray Velopack `Update.exe` isn't newer (D9).
- README: install, update, uninstall, disable, PATH repair, and the antivirus allow-list paths (now `Program Files\Tack\`).
- Review doc: mark #1 fixed, #3, #4 and #9 fixed or reduced, #6 partly.

### Phase 8: manual end-to-end check (you run it)

From a local `publish.bat` build, with the exact commands written out the way the probe's were:

1. Install through `install.ps1` pointed at the local build.
2. Check `doctor` is clean.
3. `tool add` a new tool (one UAC prompt).
4. Run it normally and from an admin shell.
5. `disable` / `enable`.
6. `update` to a second local build (one UAC prompt).
7. Uninstall.

## Open risks

- **Falcon:** real tack writes the system PATH and places unsigned shims named `node.exe` in Program Files. Expect the
  same allow-list conversation as before, now for `C:\Program Files\Tack\`.
- **Double-click MSI route:** its hooks weren't retested on 1.2.158. If they're unelevated, `tack setup` covers it.
- **Several users on one machine:** a shim added for one user shows up for everyone, as passthrough. That's fine, but
  `doctor --fix` pruning only knows the current user's names, so it asks before removing anything.
