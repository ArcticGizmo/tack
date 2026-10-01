# Design: per-directory dispatch and the trust model

*Drafted 2026-09-30 on `docs/adversarial-review`. Decision record: [ADR 0001](../adr/0001-admin-owned-shims-per-user-config.md).
Builds on the [adversarial review](../adversarial-review.md) and the [M9 spike](../../spikes/m9-msi/README.md#findings-2026-09-29).*

> **Revision (2026-10-01): the recommendation is now option D, in [ADR 0002](../adr/0002-shims-on-the-user-path.md).**
> The analysis below still stands. What changed is how much weight R7 gets. Under legacy UAC an elevated process
> reads your user PATH as well as the system PATH, so where the shims live makes no difference to your elevated
> sessions (§5.3). The boundary tack has to protect is other accounts and SYSTEM, and having nothing on the system
> PATH meets that without any elevated code. So:
>
> - **R7** is relaxed: a pin doesn't beat a tool on the system PATH, but tack reports when a name is shadowed and
>   leaves moving that tool to you.
> - **R10** is replaced: tack writes only its own entries in the user PATH, and never writes the system PATH.
> - **D**'s R4 cell under Administrator Protection is corrected to ⚠️. The elevated account doesn't see your user
>   PATH, so tack isn't there, which is the same result as B's passthrough.

This document steps back from the M9 implementation plan and asks what tack is for, what it has to promise,
which designs could deliver that, and how an attacker would abuse each one. It compares tack with nvm-windows,
fnm and pyenv-win throughout, because "is this worse than what people already run?" is the question that
decides whether a risk is tack's to fix.

## 1. What tack is for

> tack helps version tools by directory **for user processes**, **without changing how you call commands**.

- **By directory:** the version comes from where the call is made (`tack.yml`, zones, defaults), not from which
  shell you're in.
- **For user processes:** your terminal, your IDE, your build steps, your scheduled tasks, all running as you.
  SYSTEM, services and other people's accounts are **out of scope**: tack should be invisible to them.
- **Without changing how you call commands:** `node`, not `tack exec node` or `nvm use`. That includes callers
  that never ran a shell hook, like Visual Studio or a pre-build event.

This narrows the original scope (the [scope plan](../scope-and-implementation-plan.md) and the README mention
services). Services as SYSTEM are now explicitly out, because serving them is what made 0.1.x dangerous.

## 2. Requirements

| # | Requirement | Why |
|---|---|---|
| R1 | Callers run a bare `node`; nothing about the call changes | the product |
| R2 | The version depends on the caller's working folder | the product |
| R3 | It reaches processes that never ran a shell hook (IDEs, GUI tools, build steps, scheduled tasks as you) | the reason tack exists instead of fnm or mise |
| R4 | Your normal and elevated processes get the same answer | one mental model: "node resolves to this for me, always" |
| R5 | Other accounts (SYSTEM, services, other users) see no change | out of scope, so it must be invisible |
| R6 | tack adds no route from one account to another, or to SYSTEM | the critical 0.1.x finding |
| R7 | A pin beats tools installed for all users (a system-wide Node, `Git\cmd`, a Python on the system PATH) | otherwise a pin silently doesn't apply |
| R8 | Everyday config changes (versions, zones, settings) need no UAC prompt | usability, and avoiding UAC fatigue |
| R9 | Malware running as you can't use tack to get into your elevated processes | nice to have (see §5.3) |
| R10 | tack never writes the user PATH | standing rule (tack only edits the system PATH, via UAC) |

R4 and R9 pull against each other, and how hard they pull depends on how Windows elevates. §5.3 covers this.

## 3. How Windows finds a command

These facts drive every option below.

- **A process's PATH is the system PATH followed by the user PATH.** Both come from the registry when the
  environment is built: `HKLM\...\Session Manager\Environment` then `HKCU\Environment`. So a folder on the user
  PATH always loses to one on the system PATH (R7). Entries are stored unexpanded (`%NVM_HOME%`), and a variable
  that isn't defined for an account stays as literal text for that account.
- **cmd and PowerShell** look for a bare command in the current folder (cmd only) and then in PATH order, trying
  each `PATHEXT` extension. This is how most scripts, build tools and IDE tasks start tools.
- **`CreateProcess` with no path** searches the application's folder, the current folder, System32, the Windows
  folder and then PATH. So a program calling `CreateProcess("net.exe")` gets System32's, but a batch file calling
  `net` goes through PATH order first.
- **DLL loading** falls back to the PATH folders when a DLL isn't found anywhere earlier. Services that try to
  load DLLs that aren't installed are a well-known route from "can write a folder on the system PATH" to
  "code runs as SYSTEM". **This makes a writable folder on the system PATH dangerous even if nothing ever runs a
  command from it.**
- **Elevation.** Under the default UAC mode (legacy admin approval mode, `TypeOfAdminApprovalMode=1`), an
  admin's normal and elevated processes are the **same account** with the same profile, `HKCU` and
  `%LOCALAPPDATA%`. Microsoft doesn't treat that elevation as a security boundary. Under **Administrator
  Protection** (`TypeOfAdminApprovalMode=2`, shipped 2026-08-27 in KB5120998, off by default, with Microsoft
  aiming to turn it on by default), elevated processes run as a hidden, system-managed account with its own SID,
  profile and registry hive, and Microsoft *does* intend that to be a boundary.

## 4. What this machine already looks like

`tools/audit-path.ps1` reads both PATH values and, from the ACLs alone (no probe files), reports who can add a
file to each folder, or create it if it's missing. It was run on the maintainer's machine on 2026-09-30, from a
normal (non-elevated) shell, with **no tack entries on either PATH**. The folder names are grouped here rather
than listed, because this is a work machine and the raw list reads as a map of where to plant files. Re-run the
script to see your own.

### System PATH: 28 entries, 12 changeable by a non-admin

| Count | What | Who can change it | How |
|---|---|---|---|
| 7 | Tools and script folders installed **directly under `C:\`** (a Git, a GitHub CLI, a Python and its `Scripts`, a personal scripts folder) | **any authenticated user** | `C:\` grants Authenticated Users *create folder*, and every folder created under it inherits *Modify* for Authenticated Users. Confirmed with `icacls`. |
| 3 | Folders of an SDK that's no longer installed, also under `C:\` | **any authenticated user** | The folders are missing, and anyone can create them (a "phantom" PATH entry). |
| 1 | nvm-windows' `%NVM_SYMLINK%` | **any authenticated user** | See below. |
| 1 | nvm-windows' `%NVM_HOME%` | **the maintainer** | Defined at machine scope as a folder inside the maintainer's own profile. |
| 16 | `System32`, `Program Files\...` | admins only | |

The nvm-windows entries show three separate problems in two PATH entries:

- `NVM_HOME` is set at **machine** scope to `%USERPROFILE%\AppData\Local\nvm`, so every account, SYSTEM included,
  searches a folder the maintainer can write. That's the 0.1.x tack finding, made by another tool.
- `NVM_SYMLINK` is set at **user** scope only. For SYSTEM and other users, the system PATH entry
  `%NVM_SYMLINK%` stays literal text, which is a relative path.
- The symlink itself (`C:\nvm4w\nodejs`, pointing into the maintainer's profile) is in a folder under `C:\`.
  Any authenticated user can delete it and create their own `nodejs` folder, which the maintainer's processes
  (and anyone whose `NVM_SYMLINK` points there) then search.

### User PATH: 20 entries

16 are folders the maintainer can write (normal for per-user installs, including Windows' own
`%LOCALAPPDATA%\Microsoft\WindowsApps`), 2 are missing folders the maintainer could create, and **2 can be
changed by any authenticated user** (nvm-windows' `C:\nvm4w\nodejs`, and a missing folder under `C:\`). Those two
let *another* user's code run in the maintainer's processes.

### What it means for tack

- A writable system PATH folder is **not unusual**, but it is always a finding. The machine already has
  12 routes to SYSTEM before tack, and tack must not add a thirteenth.
- A writable **user** PATH folder is the norm and gives nothing to malware running as you: it could just edit
  `HKCU\Environment`. Only folders writable by *other* users matter there.
- nvm-windows, the tool tack is most often used alongside, is itself the source of three of these entries.
  `tack doctor` already names it as a shadowing problem. It could also report these (§8).

## 5. Threat model

### 5.1 Attackers

| # | Attacker | Starts with |
|---|---|---|
| X1 | Malware running as you, at normal (medium) integrity | everything your normal session can do: your profile, `HKCU`, your user PATH, starting UAC prompts |
| X2 | Another standard user on the same machine | their own account, plus anything any authenticated user can write (§4) |
| X3 | An untrusted repo you cloned | the files in it: `tack.yml`, `.nvmrc`, `.python-version`, scripts |
| X4 | Someone who controls a shared folder (network share, another volume, a folder another user created) | files in folders above your working folder |
| X5 | Whoever can change a release (the GitHub account, the CI pipeline) | the update feed |

### 5.2 What counts as a finding

The same rule as the adversarial review: it counts only if tack gives the attacker something they didn't
already have. X1 gets nothing from an attack that ends in "code runs as you". A finding is code running as
SYSTEM, as another user, in your elevated processes (with the caveat in §5.3), or untrusted data becoming a
command.

### 5.3 The elevated-process question

What X1 can do to your elevated processes depends on the UAC mode.

- **Legacy admin approval mode (today's default):** elevated processes are you. They read your profile, your
  user PATH, your `$PROFILE`, your per-user Python packages and your `.npmrc`, all writable by X1. Microsoft
  doesn't treat this as a boundary, and no developer tool defends it. tack reading your config there adds
  nothing X1 couldn't do by editing your user PATH. **R4 is free; R9 isn't achievable by tack alone.**
- **Administrator Protection:** elevated processes are a different account with their own profile. Your user
  PATH, profile and tack config aren't visible to them, and Microsoft intends X1 not to cross. **R9 becomes a
  real boundary, and R4 is no longer free:** giving the elevated account your pins means trusting something your
  normal session can write, unless both the config and every tool folder it names are admin-owned.

### 5.4 Attacks

Each row is an attack, who gains, and whether it works against tack 0.1.x, option B (the M9 plan), the
maintainer's proposal C1 (admin-owned config), and two reference points **as installed on the maintainer's
machine**: nvm-windows, and the per-user tools fnm and scoop. The reference columns only record what the §4
audit observed. "not checked" means nothing on this machine showed it either way. "n/a" means the tool doesn't
have the feature being attacked.

| # | Attack | Attacker → gains | 0.1.x | B | C1 | nvm-windows (as installed) | fnm, scoop (as installed) |
|---|---|---|---|---|---|---|---|
| T1 | Plant an exe or DLL in a writable **system** PATH folder | X1 → SYSTEM, other users | **yes** (shims folder in profile) | no | no | **yes**: `%NVM_HOME%` is on the system PATH and points into one user's profile | no: user PATH only |
| T2 | Create a missing system PATH folder | X1, X2 → SYSTEM | no | no | no | related: `%NVM_SYMLINK%` isn't defined for SYSTEM, so that entry is a relative path for it | no |
| T3 | Repoint a symlink or junction that's on PATH | X2 → you and anyone using it; X1 → SYSTEM | no | no | no | **yes**: `C:\nvm4w` gives Authenticated Users *Modify* | no |
| T4 | Edit the config the shim trusts, so another account runs your choice | X1 → SYSTEM, other users | **yes** (shim read the folder above itself) | no (token's own config only) | no | not checked | not checked |
| T5 | Overwrite a tool binary in your profile | X1 → your elevated shells | yes | legacy UAC: yes, same as every per-user tool; Admin Protection: no (passthrough) | yes, unless tool folders are admin-owned | **yes, and also → SYSTEM and other users** through the system PATH entries | legacy UAC: yes; Admin Protection: expected no (separate profile, inferred) |
| T6 | Edit your tack config | X1 → your elevated shells | yes | legacy UAC: yes (not a boundary); Admin Protection: no | no | n/a | n/a |
| T7 | Ask the elevated helper to do something (confused deputy) | X1 → admin writes | **yes** (`apply-machine-path` took any folder) | no: helper takes only validated shim names and works out its own paths | **yes**: the helper must accept config changes, so `tool add node@20 C:\evil` gets a routine tack prompt | not checked (`nvm use` needs admin or Developer Mode to change the symlink) | none observed |
| T8 | Swap the elevated binary before the next UAC prompt | X1 → admin | **yes** (`tack.exe` in the profile) | no (Program Files) | no | not checked | none observed |
| T9 | Junction a folder that elevated code writes into | X1 → admin file writes | **yes** (reshim, PATH backup) | no (elevated code writes only under Program Files) | no | not checked | none observed |
| T10 | Apply an update from a normal process, leaving `current\` writable | X1 → admin next time | n/a | prevented (updates elevated only; spike) | same as B | not checked | n/a (per-user install) |
| T11 | A repo file picks the version | X3 → choose among your registered versions, including ones carrying env vars | yes (limited) | yes (limited) | yes (limited) | not checked | not checked |
| T12 | A `tack.yml` planted above your working folder on a shared drive | X4 → as T11 | yes (limited) | yes (limited) | yes (limited) | not checked | not checked |
| T13 | Arguments injected through a `.cmd`/`.bat` in the call chain (BatBadBut) | X3 → command execution as you | **yes** (fixed in review #2) | no | no | not checked | not checked |
| T14 | An auto-detected shim name shadows a system command (`curl`, `ssh`, `tar`) | availability for everyone; confusion | yes | reduced: other accounts pass through; names validated | same as B | n/a | n/a |
| T15 | Get you to approve a UAC prompt for the wrong thing (prompt fatigue) | X1 → admin | first run + PATH fixes | rare: install, update, new command names | **every** config change | likely on `nvm use`, since creating a symlink needs admin unless Developer Mode is on (not checked) | none |
| T16 | Replace a release | X5 → everyone who updates | yes (checksums only) | yes (checksums only), elevated update | same as B | not checked | not checked |

Reading the table:

- **B closes every route tack itself opened** (T1, T4, T7, T8, T9) and never makes things worse than the
  baseline for the rows it can't close.
- **T5 and T6 are the same attack from X1's side.** Protecting the config (C1) and leaving the tool folders
  writable only moves where X1 writes. That's why C1 alone doesn't reach R9.
- **C1 opens T7 and makes T15 much worse,** because an elevated writer of user-supplied config is a confused
  deputy that X1 can call, behind a prompt the user sees all the time.

### 5.5 Comparison with other tools

What the §4 audit shows about the tools installed on the maintainer's machine:

| Tool | PATH scope | Who can write what's on PATH | Your processes | Elevated (legacy UAC) | SYSTEM, services, other users |
|---|---|---|---|---|---|
| nvm-windows | **system and user** (`%NVM_HOME%`, `%NVM_SYMLINK%` on both) | you (`NVM_HOME` in your profile); any user (`C:\nvm4w`) | global version, via the symlink | same as normal | **run node from your profile; exposed to T1, T3, T5** |
| fnm | user (its winget package folder) | you | per shell, via the profile hook | same, if the hook runs | unaffected |
| scoop (per-user) | user (`%USERPROFILE%\scoop\shims`) | you | shims | same | unaffected |
| Windows' app aliases | user (`%LOCALAPPDATA%\Microsoft\WindowsApps`) | you | aliases | same | unaffected |
| tack 0.1.x | **system** (shims and `current\` in your profile) | you | per directory | same | **exposed to T1 and T4** |
| tack under B | system (Program Files) | admins only | per directory | same | passthrough |

Takeaways:

- **Per-user tools** (fnm and scoop here; pyenv-win, Volta and mise are expected to work the same way, but that
  isn't verified) keep everything on the user PATH. They can't reach SYSTEM or other users, and they can't beat a system-wide install (R7). Their
  elevated behaviour is exactly B's: same account, same writable files under legacy UAC; a separate profile
  under Administrator Protection.
- **nvm-windows** is the outlier. It puts per-user folders on the **system** PATH, which is the shape of the
  critical 0.1.x finding. tack under B is strictly safer than the tool it most often sits beside.
- **B matches the per-user tools on trust and beats them on reach.** Its only system PATH entries are
  admin-owned, and it still wins over system-wide installs.

**Not yet covered:** pyenv-win isn't installed on this machine, so its row, and the "not checked" cells in §5.4
(repo version files, batch shims, nvm-windows' elevation path, update channels), still need checking against
each tool's own documentation. A background research pass was started, but its report was blocked partway
through and isn't used here.

## 6. Options

| | Option | Shims | Config | Tool folders |
|---|---|---|---|---|
| A | Per-user install (0.1.x) | profile, on the system PATH | profile | anywhere |
| **B** | **Admin-owned shims, per-user config (M9)** | Program Files, on the system PATH | profile | anywhere |
| C1 | Admin-owned config, one per account (the maintainer's proposal) | Program Files | Program Files, per account, elevated writes | anywhere |
| C2 | Admin-owned config, one per machine | Program Files | Program Files, shared, elevated writes | anywhere |
| P | Protected mode: C1 plus admin-owned tool folders plus a link from the elevated account back to you | Program Files | Program Files, per account | admin-owned only |
| D | Shims on the user PATH | profile, on the user PATH | profile | anywhere |
| E | Shell activation (a profile hook edits the shell's own PATH on `cd`) | none | profile | anywhere |

## 7. Evaluation

✅ meets it · ⚠️ partly · ❌ doesn't. "Legacy" and "AP" are the two UAC modes from §3.

| | R1 | R2 | R3 IDE reach | R4 same when elevated | R5 invisible to others | R6 no cross-account | R7 beats system installs | R8 no UAC for config | R9 elevated safe from X1 | R10 no user PATH |
|---|---|---|---|---|---|---|---|---|---|---|
| A | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ | ✅ |
| **B** | ✅ | ✅ | ✅ | legacy ✅, AP ⚠️ (passthrough) | ✅ | ✅ | ✅ | ✅ | legacy ❌ (baseline), AP ✅ | ✅ |
| C1 | ✅ | ✅ | ✅ | legacy ✅, AP ⚠️ (needs the link) | ✅ | ✅ | ✅ | ❌ | ❌ (T5, T7) | ✅ |
| C2 | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ unless tool folders are admin-owned | ✅ | ❌ | ❌ | ✅ |
| P | ✅ | ✅ | ✅ | ✅ if the link exists | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |
| D | ✅ | ✅ | ✅ | legacy ✅, AP ⚠️ (tack absent) | ✅ | ✅ | ❌ (reported) | ✅ | legacy ❌, AP ✅ | ❌ (R10 replaced 2026-10-01) |
| E | ⚠️ needs a hook | ✅ | ❌ | legacy ✅, AP ❌ | ✅ | ✅ | ✅ in hooked shells | ✅ | legacy ❌, AP ✅ | ✅ |

- **A** is out (R6).
- **D** and **E** give up something tack exists for (R7, R3).
- **C2** brings back the critical finding unless it also restricts tool folders, at which point it's P for
  everyone.
- **C1** costs R8 and buys nothing over B under legacy UAC. Under Administrator Protection it's one of the three
  parts of P.
- **B** meets everything except R4 under Administrator Protection (where it fails safe) and R9 under legacy UAC
  (where nothing can).
- **P** meets everything except R8, but costs "bring your own installs" for anything you want pinned in elevated
  shells, and depends on an unverified Windows capability.

## 8. Recommendation

*Superseded on 2026-10-01: adopt D ([ADR 0002](../adr/0002-shims-on-the-user-path.md)) instead of B. Points 3
and 4 still apply. Point 2 is deferred until Administrator Protection matters.*

1. **Adopt B** ([ADR 0001](../adr/0001-admin-owned-shims-per-user-config.md)) and carry on with the M9 plan
   from phase 4. Nothing in this review changes phases 1 to 3.
2. **Spike protected mode (P) before Administrator Protection becomes the default.** The question is whether a
   shim running under the system-managed admin account can reliably and safely identify the primary user
   whose config applies. If it can, P is an opt-in mode with its own ADR. If it can't, document that elevated
   shells pass through under Administrator Protection, like every other per-user tool.
3. **Extend `tack doctor`** with the audit this document ran:
   - tack's own folders (shims, `current\`) aren't writable by you (already in M9 phase 7),
   - registered tool folders that are writable, shown as information under legacy UAC and as a warning if
     Administrator Protection is on and protected mode is in use,
   - system PATH entries that any user, or you, can write or create, starting with nvm-windows' `NVM_HOME` and
     `NVM_SYMLINK` at the wrong scope. Report them; don't fix them, since they belong to other tools.
4. **Reword the README's scope** to match §1: user processes, not services.

## 9. For the maintainer, outside tack

The audit found routes to SYSTEM on this machine that have nothing to do with tack. Fixing them means changing
ACLs and PATH entries owned by other software, so they're listed here rather than changed:

- remove *Modify* for Authenticated Users from the folders under `C:\` that are on the system PATH, or reinstall
  those tools under `C:\Program Files`,
- remove the three phantom SDK entries from the system PATH,
- move nvm-windows' `NVM_HOME` to user scope (or reinstall nvm-windows with an admin-owned root), and lock down
  `C:\nvm4w`.

This is a managed work machine, so raise it with the security team rather than changing it yourself.
