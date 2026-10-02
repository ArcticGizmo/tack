# ADR 0001: Admin-owned shims on the system PATH, per-user config

- **Status:** Superseded by [ADR 0002](0002-shims-on-the-user-path.md) (2026-10-01). Proposed 2026-09-30, never
  accepted. Kept for its analysis, which ADR 0002 relies on.
- **Deciders:** the maintainer
- **Design doc:** [Per-directory dispatch and the trust model](../design/dispatch-trust-model.md)
- **Replaces:** the "option B" decision recorded in [adversarial-review.md #1](../adversarial-review.md#decision-2026-09-29-option-b), which this ADR restates with the full comparison

## Context

tack versions tools by directory for **your own processes** without changing how anything calls them. `node`
typed in a terminal, launched by an IDE, or run by a build step as you, gets the version that directory pins.

That needs three things at once:

1. A real file named `node.exe` that is found **before** any other `node.exe`, including ones installed for
   all users (a system-wide Node, a `C:\Program Files\Git\cmd`). Windows puts system PATH entries before user
   ones, so tack's shims have to go on the **system** PATH.
2. A decision, made per call, about which version to run. That comes from a config you edit often.
3. No new way for code running as one account to run code as another account, including SYSTEM.

Release 0.1.x broke (3): its shims folder was in the user's profile, first on the system PATH, so any program
running as that user could make SYSTEM or another user run its code. The machine audit in the design doc shows
this is common. On the maintainer's own machine, 12 of 28 system PATH entries can be changed by a non-admin
before tack is even installed, and nvm-windows alone adds two of them.

The maintainer also proposed keeping the config in an admin-owned folder, with every write elevated and every
read unprivileged, so that "node resolves to this for me, always" holds in normal and elevated processes alike.

## Decision

**The binaries and shims are admin-owned. The config stays per-user and doesn't need admin to change.**

- tack is installed per-machine (Velopack MSI) to `C:\Program Files\Tack\`. Shims live in
  `C:\Program Files\Tack\shims`, first on the system PATH. Users can read and run them but can't write them.
- Each account's config (`config.json`, the compiled `resolved.json`, logs) lives in that account's own
  `%LOCALAPPDATA%\tack\`. Editing it needs no elevation.
- The shim reads **only** the config of the account it's running as, found from the process token. An account
  without a config (SYSTEM, services, other users) gets plain passthrough, as if tack weren't installed.
- UAC is needed only to install, update, uninstall, and to stamp a shim for a **new command name**.
- `tack doctor` reports writable folders that matter: shims or install folders a normal user can write, and
  registered tool folders that are writable (see Consequences).

## Why

**Elevated shells already behave the same, under today's default UAC.** In legacy admin approval mode
(`TypeOfAdminApprovalMode=1`, the Windows default and what the maintainer's machine runs; `BUILTIN\Administrators`
is deny-only in the normal token), accepting a UAC prompt gives an elevated process for **the same account**: same
profile, same `%LOCALAPPDATA%`, same `resolved.json`, same answer. The single mental model the maintainer asked
for, "node resolves to this for me, always", already holds. Only a *different* account (typing another admin's
password at the prompt, SYSTEM, a service) gets passthrough, and that's what "for user processes" means.

**Under Administrator Protection it doesn't, and B fails safe.** Administrator Protection
(`TypeOfAdminApprovalMode=2`, shipped in KB5120998 on 2026-08-27, off by default, which Microsoft intends to make
the default) runs elevated processes as a hidden, system-managed account with its **own** profile and registry
hive. An elevated shell then has a different `%LOCALAPPDATA%` and no tack config, so under B it gets passthrough.
That is safe, but it breaks "the same for me, always" in exactly the case the maintainer cares about. Every tool
that keeps state in your profile or your user PATH (fnm and scoop on the maintainer's machine, for example) has
the same gap there. Closing it for tack is an open question (below), not a reason to drop B: under Administrator
Protection, UAC becomes a real boundary, so the one thing tack must not do is let the elevated side trust a file
your normal session can write.

**An admin-owned config wouldn't stop the attacker it targets.** The attacker it's meant to stop is malware
running as you, trying to get into your elevated processes. Against that attacker:

- The registered folders are still yours to write. On the maintainer's machine, nvm-windows, fnm and scoop all
  install into the profile. Malware doesn't need to edit the config when it can overwrite `%LOCALAPPDATA%\nvm\v20.11.0\node.exe`.
- The elevated writer is a confused deputy. It has to accept config changes from your normal-level session,
  so malware can ask it for `tool add node@20 C:\evil` and get the same routine "tack.exe wants to make
  changes" prompt you approve several times a week.
- Your user PATH, `$PROFILE`, `HKCU\Environment`, user-scope Python site-packages and `.npmrc` are all
  writable by you and all trusted by your elevated shells. Microsoft doesn't treat same-account elevation as a
  security boundary. tack can't make it one.

**It would cost a lot for that small gain.** Every `tool add`, `zone add`, `use --global`, `disable` and `log on`
would need a UAC prompt. That trains you to approve tack prompts without reading them, which is the opposite of
what a UAC prompt is for. A shared admin folder also needs per-account ACLs, or everyone's config becomes
readable by everyone, including per-version environment variables that may hold tokens.

## Why not elevate every write?

The obvious objection: malware running as you edits `resolved.json` (or registers a tool named after something
common) and waits for a privileged process to run it. Whether that works depends on **who** the privileged
process runs as.

**SYSTEM, services and other users: it doesn't work, and tack makes sure of that.** The shim never reads the caller's
config for another account. A SYSTEM task running `net` or `node` finds no config for SYSTEM and passes straight
through to the real binary. Your registrations can't change what runs for anyone else, so elevating config
writes would add nothing here.

**Your own elevated processes, under legacy UAC: it works, and it's accepted.** An admin shell there is your
account with more rights, and Windows already lets code at your normal level decide what runs in it. Without tack,
malware running as you can:

| Door | What it gives the attacker in your elevated sessions |
|---|---|
| Your PowerShell `$PROFILE` (in your Documents) | runs at the start of every PowerShell session, elevated ones included; one line can put any folder first on `$env:PATH` |
| Your PowerShell modules folder | modules there load, by name, into elevated sessions |
| `HKCU\Software\Microsoft\Command Processor\AutoRun` | runs at the start of every `cmd.exe`, elevated ones included (unless started with `/d`) |
| `HKCU\Environment` | user variables apply to elevated processes too. `NODE_OPTIONS=--require C:\evil.js` changes what every `node` does, exactly like editing tack's config. User PATH entries come after the system PATH, so they only beat commands the system PATH doesn't have. |
| Windows Terminal's `settings.json` (in your `%LOCALAPPDATA%`) | sets the command line of every profile, including the one you "Run as administrator" |
| The tool folders themselves (for example `%LOCALAPPDATA%\nvm\v24.20.0\node.exe`) | replace the binary; no config change needed. An admin-owned config doesn't protect this. |

All of these are in the same class as the tack attack: plant something as you, then wait for you to elevate.
With UAC set to always prompt (the maintainer's machine has `ConsentPromptBehaviorAdmin=2`), the silent bypasses
are blocked and this class is what remains. Microsoft doesn't treat it as a boundary, because it can't be closed
without separating the profiles, which is what Administrator Protection does.

So elevating tack's config writes would close one door and leave the others open, and the attacker's result
wouldn't change. It would also cost something:

- a UAC prompt on every `tool add`, `zone add`, `use --global`, `disable` and `log on`, which trains you to
  approve "tack.exe wants to make changes" without reading it,
- a confused deputy: the elevated writer has to accept changes from your normal session, so malware can request
  `tool add node@20 C:\evil` itself and get the same prompt you approve every day.

**Under Administrator Protection the answer changes.** The elevated account has its own profile, so every door in
the table closes, and "wait for you to elevate" really does cross a boundary. B stays safe there because the
elevated account has no tack config and passes through. Regaining your pins there is the open question below, and
it does need admin-owned config.

**Cheap hardening that fits B** narrows the elevated-shell route without a UAC prompt for every change:

- refuse, or require confirmation for, shim names that already exist under `%SystemRoot%` (`powershell`,
  `net`, `curl`), so a registration can't take over something common (review #5),
- have the elevated stamp step list the new command names before writing them, so a surprise prompt shows what
  it's for,
- have `tack doctor` report registered tool folders you can write.

## Consequences

**Good:**

- Nothing tack puts on the system PATH can be written by a non-admin. tack adds no route from one account to
  another, or to SYSTEM.
- Under today's default UAC, the same account gets the same resolution whether elevated or not. Other accounts
  never see tack's decisions.
- Day-to-day config changes need no UAC prompt.

**Accepted:**

- Your elevated processes trust files your normal-level processes can write (your config and your registered
  tool folders). This is the same trust per-user tools (fnm and scoop here) and your user PATH already rely on,
  and tack adds nothing beyond it. `tack doctor` reports writable registered folders so you can see this rather
  than assume it.
- A new command name needs one UAC prompt. If you decline, the tool is configured but not intercepted until
  `tack setup`.
- Stale shims stay until `doctor --fix`, and a shim added for one account appears (as passthrough) for all.
- If you elevate by typing a **different** account's credentials, or Administrator Protection is on, the
  elevated session gets passthrough. It runs as another account, which has no tack config.

**Needed from the implementation (already in the M9 plan):**

- The shim never reads a config other than its own account's (M9 phase 1, done).
- Elevated code works out every path from its own Program Files location, takes no folder arguments, and
  validates shim names (M9 phases 3 and 4).
- Updates are applied only from an elevated process (M9 phase 5).

## Alternatives considered

| Option | Summary | Why not |
|---|---|---|
| A. Per-user install (0.1.x) | shims in `%LOCALAPPDATA%`, on the system PATH | Any program running as the user gets SYSTEM. Critical. |
| C1. Admin-owned config, one per account | the maintainer's proposal: config under Program Files, keyed by account, elevated writes | Under today's UAC, gives the same resolution as B and doesn't stop malware running as you (writable tool folders, confused deputy), for a UAC prompt on every change. Under Administrator Protection it's a necessary part of protected mode (see the open question), but not enough on its own. |
| C2. Admin-owned config, one per machine | one config every account follows, elevated writes | SYSTEM and other users would run binaries from the maintainer's profile, the same critical hole as A unless every registered folder is admin-owned. Breaks "invisible outside your account". |
| D. Shims on the user PATH | the scoop model (and, unverified, pyenv-win's, Volta's and mise's) | Loses to anything on the system PATH (system-wide Node, Git's `cmd`), so a pin silently doesn't apply. Also needs tack to write the user PATH, which is ruled out. |
| E. Shell activation | the fnm, `mise activate`, nvm-sh model: a profile hook edits the shell's own PATH | Doesn't reach IDEs, GUI tools or scheduled tasks. That reach is why tack exists. |

## Open question: "the same for me, always" under Administrator Protection

B passes through in elevated shells once Administrator Protection is on. Giving those shells your pins without
creating a UAC bypass needs **all three** of these, which together are the maintainer's proposal (C1) plus a
restriction on where tools live:

1. **The config is admin-owned** (C1), so your normal session can't change what the elevated side runs.
2. **Every registered tool folder is admin-owned.** Otherwise malware overwrites
   `%LOCALAPPDATA%\nvm\v20.11.0\node.exe` instead of the config. Installs that version managers such as nvm-windows,
   fnm or scoop make into your profile couldn't be registered for elevated use.
3. **The shim can map the elevated account back to you.** Under Administrator Protection the elevated token has a
   different SID and profile, so the shim needs a trustworthy link from that token to the primary user whose
   config applies. Whether Windows exposes one is **unverified** and needs a spike.

That would be a new, opt-in "protected mode", recorded in its own ADR once the spike answers point 3. It doesn't
change B: whatever protected mode turns out to be, the shims and binaries stay admin-owned and the shim still
never trusts a user-writable file in an elevated process.

## When to revisit

- **Administrator Protection becomes the default,** or the maintainer turns it on. Run the spike above and decide
  on protected mode.
- **Managed, organisation-wide pinning.** If tack gains a mode where IT sets versions for everyone, C2 fits it,
  provided every registered folder is admin-owned and tack refuses any that aren't. It shares most of its
  machinery with protected mode.
- **Code signing arrives.** It doesn't change this decision, but it makes the UAC prompt show a publisher,
  which matters more under C than B.
