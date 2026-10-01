# ADR 0002: Shims on the user PATH, nothing on the system PATH

- **Status:** Proposed (2026-10-01)
- **Deciders:** the maintainer
- **Supersedes:** [ADR 0001](0001-admin-owned-shims-per-user-config.md) (Proposed, never accepted)
- **Design doc:** [Per-directory dispatch and the trust model](../design/dispatch-trust-model.md). Its analysis
  still stands; its 2026-10-01 revision note points here.

## Context

ADR 0001 put tack's shims first on the **system** PATH, in an admin-owned folder, so that a pin could beat tools
installed for all users (R7 in the design doc). That needs a per-machine MSI, an elevated install, update and
uninstall, an elevated helper to stamp new command names, and rules to stop that helper becoming a confused deputy.

While reviewing a proposal to add a second shims folder on the user PATH (see Alternatives), one fact turned out to
decide the question: **under the default UAC mode, an elevated process reads your user PATH too.**

### How an elevated process finds a command

- **Default UAC (legacy admin approval mode).** Accepting a UAC prompt starts a process as the **same account**
  with more rights. Its environment is built from the same `HKLM` and `HKCU` values as everything else you run, so
  its PATH is the system PATH followed by your user PATH.
- **System entries always come first.** Writing to the user PATH can't shadow a `git` that's on the system PATH.
  It can only shadow commands that exist only on the user PATH, or add new ones.
- **That ordering doesn't protect elevated sessions,** because code running as you doesn't need the PATH. It can
  add one line to your `$PROFILE`, set `NODE_OPTIONS` in `HKCU\Environment`, change the elevated profile's command
  line in Windows Terminal's settings, or overwrite a binary in a tool folder in your profile. ADR 0001 lists
  these doors in [Why not elevate every write?](0001-admin-owned-shims-per-user-config.md#why-not-elevate-every-write).
- **Microsoft doesn't treat same-account elevation as a security boundary.** Under legacy UAC, *no* choice tack
  makes protects your elevated sessions from code running as you (R9 in the design doc).
- **Administrator Protection** runs elevated processes as a separate, system-managed account with its own profile
  and `HKCU`. Your user PATH, profile and tack config aren't visible to it. That makes elevation a real boundary,
  and anything tack keeps per-user simply isn't there.

### Where tack's defences do pay off

- **Other accounts and SYSTEM.** Every account searches the system PATH. A folder on it that a normal user can
  write lets that user's code run as SYSTEM, through a planted exe or a DLL that a service fails to find elsewhere.
  That was the critical 0.1.x finding, and this boundary is real in every UAC mode.
- **Administrator Protection.** Once elevation is a boundary, the elevated side must not trust files your normal
  session can write.

Neither needs tack on the system PATH. Both are met most simply by tack having **nothing** there. The user PATH
is per-account, so SYSTEM, services, other users and an Administrator Protection elevated account never search
tack's folders.

## Decision

**tack lives entirely in your profile and puts nothing on the system PATH.** In effect this is 0.1.x's per-user
install, with the shims on the user PATH instead of the system PATH.

- tack is installed per-user (Velopack `Setup.exe`, `%LOCALAPPDATA%\Tack\`). Install, update and uninstall need
  no UAC.
- Shims live in `%LOCALAPPDATA%\tack\shims`, **first on your user PATH**, ahead of Windows' app execution aliases
  (`%LOCALAPPDATA%\Microsoft\WindowsApps`). `Tack\current` goes on the user PATH after them, and the dev
  profile's shims go behind release's.
- The shim still reads only the config of the account it's running as (M9 phase 1). Other accounts shouldn't
  reach it at all now, so this is defence in depth.
- **A name that something on the system PATH already provides isn't intercepted, and tack says so** (next
  section). tack doesn't change the system PATH for you. Moving that tool is your decision.
- **The PATH rule is inverted: tack writes only its own entries in your user PATH, and never writes the system
  PATH.** This replaces design-doc R10 and the old "system PATH only" rule, and keeps that rule's spirit:
  - only tack's own entries, added by install or `tack setup` and removed by uninstall,
  - the value kind (`REG_EXPAND_SZ`) is preserved, the old value is backed up first, `setx` is never used, and
    `WM_SETTINGCHANGE` is broadcast afterwards,
  - there's no fallback from one scope to another, because there's only one scope.

## Shadowed names

cmd and PowerShell search PATH folder by folder, trying every `PATHEXT` extension in a folder before moving to
the next. So any folder on the system PATH that holds `node.exe`, `node.cmd` or `node.bat` beats tack's
`node.exe`, whatever the extensions.

The shim can't detect this, because a shadowed shim never runs. So tack checks ahead of time:

- **When:** `tool add` (including auto-detected `exposes` names), `tack doctor`, and whichever command explains
  resolution (`tack which`).
- **How:** for each exposed name, walk the PATH a new process would get: the system PATH as it expands for your
  account, then the user PATH entries that come before tack's. Report the first match.
- **What it says:** what wins, where it comes from, and what you can do about it. For example:

  ```text
  node is configured but won't be intercepted:
    C:\Program Files\nodejs\node.exe comes first (system PATH entry 4).
  To let tack manage node, uninstall the system-wide Node or take its folder off the system PATH
  (needs admin), then install the versions you want per-user and register them with tack.
  ```

- **tack doesn't fix it.** The entry belongs to other software, changing it needs admin, and SYSTEM or other
  users may depend on it. This is the design doc's §8 stance: report, don't fix.
- **Windows' own commands are refused, not just reported.** A name found under `%SystemRoot%` (`where`, `curl`,
  `tar`) can't be moved, and `CreateProcess` searches System32 before PATH anyway. This replaces review #5's
  "require confirmation".
- **Your own user PATH can shadow too,** if an installer later puts a folder ahead of tack's. `doctor` reports
  it, and `doctor --fix` moves tack's entry back to the front, which needs no admin.

Common cases are Node installed for all users (`Program Files\nodejs`), Git for Windows (`Git\cmd`), Python
installed for all users, the .NET SDK (`Program Files\dotnet`) and nvm-windows' system PATH entries.

PATHology already builds this "which folder wins for each name" report. Share that analysis rather than
writing it twice.

## Why

In the design doc's evaluation (§7), B and D differ only on R7 (beat system-wide installs) and R10 (never write
the user PATH). R10 is now replaced, which leaves R7:

| | B (ADR 0001) | D (this ADR) |
|---|---|---|
| SYSTEM, services, other users | reach the shim, which passes through (no config for them) | never reach a tack folder |
| Your elevated shells, legacy UAC | your pins; trusts files you can write (baseline) | the same |
| Your elevated shells, Administrator Protection | passthrough | tack isn't there (the same result) |
| A pin beats a system-wide install (R7) | yes | no: reported, and you move the tool |
| UAC prompts | install, update, uninstall, each new command name | none |
| Elevated code tack has to get right | elevated helper, MSI hooks, elevated update | none |
| Can shadow Windows' own commands | yes (first on the system PATH), so names are validated | no |

R7 was B's only reason to exist, and it was paid for with every elevated route in the design doc's attack table:
T7 (confused deputy), T8 (swap the binary before a prompt), T9 (junction a folder elevated code writes to), T10
(an unelevated update leaving Program Files writable) and T15 (prompt fatigue). All of them disappear when tack
never elevates. What D gives up is a pin that silently doesn't apply, and the shadow report turns that into a
visible, explained one.

What's left is unchanged by where the shims live. T5 and T6 (code running as you edits your config or your tool
folders, then waits for you to elevate) are the baseline for every per-user tool under legacy UAC, and are closed
by Administrator Protection. T11 to T13 and T16 don't depend on PATH placement.

## Alternatives considered

| Option | Summary | Why not |
|---|---|---|
| B (ADR 0001) | admin-owned shims first on the system PATH, per-user config | Above: it keeps R7 at the cost of all of tack's elevated machinery and attack surface. |
| B plus user-scope shims | a second shims folder on the user PATH; each tool picks system or user scope (default user), with best-practice checks before a tool goes system-wide (proposed 2026-10-01) | Its case rested on escalation safety, which isn't there. Under legacy UAC elevated shells see both folders, and code running as you has other ways in. Under Administrator Protection both scopes give passthrough. It keeps all of B's elevated machinery, and the shim would have to honour scope: the system shims folder is shared, so once any account stamps `node` there, every account's user-scope `node` would otherwise get system precedence. |
| C1, C2, E | as in ADR 0001 | Unchanged. |

## Consequences

**Good:**

- tack never asks for UAC and contains no elevated code.
- Nothing tack installs is visible to SYSTEM, services or other users. This holds by construction, not by ACLs.
- tack can't shadow Windows' own commands.

**Accepted:**

- A pin doesn't apply to a name that something on the system PATH provides until you move that tool. tack says
  so in `tool add` and `doctor`.
- Under legacy UAC, your elevated processes trust files your normal session can write: your config, your shims
  folder and your tool folders. That's the same as fnm, scoop and your user PATH, and Microsoft doesn't treat it
  as a boundary.
- Under Administrator Protection, elevated sessions don't see tack. Protected mode (ADR 0001's open question)
  would need admin-owned config, admin-owned tool folders and a system PATH entry again. Revisit when
  Administrator Protection becomes the default or you turn it on.
- The only route left from another account into your sessions is a tack folder that other accounts can write,
  for example if `%LOCALAPPDATA%` is redirected to a share. `tack doctor` checks for that.

## What changes in M9

- **Kept:** phases 1 to 3 (the shim reads only its own account's config; disable is a per-user setting;
  compiling is separate from stamping). Stamping becomes unelevated.
- **Dropped:** phases 4 to 6 (elevated commands, MSI hooks, elevated updates, per-machine MSI packaging) and
  decisions D2, D5, D7, D8 and D9. Per-user `Setup.exe` comes back. The MSI spike's findings are no longer needed.
- **Changed:** `WindowsEnvRegistry` swaps its machine-scope write for a user-scope one. Install and `tack setup`
  add the user PATH entries, and uninstall removes them. There's still no migration (D4): `doctor` reports
  leftover 0.1.x tack entries on the system PATH and gives the command to remove them, which needs admin.
- **New:** the shadow report and the refusal of `%SystemRoot%` names.
- **To verify in the end-to-end check:** scheduled tasks that run as you, and IDEs launched from the Start menu,
  pick up the user PATH entry. That's expected, as for any per-user tool, but it hasn't been checked for tack.

A replacement for the M9 plan from phase 4 onwards is still to be written.
