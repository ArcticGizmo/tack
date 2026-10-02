# Changelog

All notable changes to tack are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.2.1] - 2026-10-02

- `tack tool install node@lts` downloads Node or Python and registers it
- `20`, `3.12`, `latest` and `lts` pick the newest matching release
- Every download's SHA-256 is checked; a mismatch is deleted, not installed
- Python arrives with a working `pip` (python.org's zip forgot it)
- Python before 3.11 is declined: python.org publishes no checksum for it
- `tack tool available` lists what you could install, newest first
- It works offline from the cached list, and admits how old it is
- `tool remove` deletes the files of versions tack installed
- `--keep-files` hands the folder over instead
- Removal refuses while something runs from it, and names the culprit
- `tool list` marks managed versions; `tack info` says where one came from
- `tack doctor` sweeps up after interrupted installs
- `tool add` still takes installs from anywhere (nvm and fnm, you may go)
- Removing the default version picks 10.x over 9.x, as numbers do
- A `3.15` pin prefers `3.15.0` to its release candidate

---

## [v0.2.0] - 2026-10-02

- tack leaves the system PATH for your user PATH
- SYSTEM, services and other accounts never run anything from your profile
- No admin and no UAC prompts, for anything, ever
- Commands a system-wide install reaches first are named, with what to do
- Windows' own `curl` and friends are politely declined
- `tack doctor` checks nobody else can write tack's folders
- It also fails while any tack folder lingers on the system PATH
- `tack setup --remove` takes tack back off your PATH
- Removing a tool takes its shims with it
- `tack disable` is instant, and switches tack off for you alone
- `.cmd` tools get their `&`, `%` and quotes intact (npm, mostly)
- `zone add --ignore-tack-files` says what it does, unlike its predecessor
- Zones that beat a `tack.yml` forget they did; add them again

---

## [v0.1.7] - 2026-09-29

- `tool add --env NAME=VALUE` gives a version its own variables
- So `claude@work` stops logging into your personal account
- The log keeps variable names and politely forgets the values

---

## [v0.1.6] - 2026-09-25

- `tack log on` records every shim call and who made it
- Catches whatever keeps running `node` from somewhere unspeakable
- `tack log off` stops it; `tack log open` finds the evidence

---

## [v0.1.5] - 2026-09-25

- `tool add` takes the install folder as a second argument (`--path` was always forgotten anyway)
- `.` and `.\` now register as the same folder

---

## [v0.1.4] - 2026-09-25

- tack keeps its hands off your user PATH (system PATH only, as promised)
- Install asks for UAC once to join the system PATH
- `tack setup` retries it, should you have clicked No
- Uninstall cleans up its system PATH entries on the way out

---

## [v0.1.3] - 2026-09-25

- `tack.exe` is a third of its former size (24 MB of runtime it never called, gone)

---

## [v0.1.2] - 2026-09-25

- `tack tool list` shows every version with the folder it runs from
- Registered folders that have vanished are flagged in red
- `tack tool list --expand` for paths that copy out in one piece, not table-cell-sized chunks

---

## [v0.1.1] - 2026-09-25

- `npx` works again (tack had been trying to run node's bash script)

---

## [v0.1.0] - 2026-09-25

The first tack: per-directory tool dispatch that reaches the processes shell hooks can't.

- Per-directory tool dispatch via shims on PATH - a bare `node`/`python` resolves to the version the directory is supposed to use
- Reaches processes you didn't launch from a shell (Visual Studio, Rider, background runners, scheduled tasks) - the whole reason tack exists
- Sibling commands follow their tool: pin `node@20` and `npm`/`npx` there are node 20's too
- `tack.yml` project files, discovered by walking up from the current directory and picked up the moment they exist - no reshim required
- Zones: pin a directory, and everything under it, to a tool version without committing a thing to the repo. The deepest zone wins, and since zones are plain directories there's never a tie to argue about
- Zones can beat a repo's `tack.yml`, for when the machine's rule has to win
- `none` zones switch tack off for a tool (`node@none`) or for every tool (`none`) in a directory tree, so commands fall through to whatever is next on PATH
- Resolution precedence you can actually explain: env override (`TACK_<TOOL>_VERSION`), zone that beats `tack.yml`, `tack.yml`, zone, default, then passthrough
- Versions match by dotted prefix, so `python: "3.12"` means the highest `3.12.x` you have (and never `3.121`)
- Passthrough when nothing matches - tack stays invisible where it isn't configured
- `tack tool add` / `remove` / `list` - bring your own installs. `add` finds the tool on PATH for you (expanding `%NVM_HOME%`-style entries) and works out which commands it provides; `remove` is an interactive picker that tidies up defaults and flags any zone left pointing at nothing
- `tack zone add` / `remove` / `list`, with an interactive picker for removal
- `tack info` and `tack which` - what a directory resolves to, the exact binary that will run, and the rule that won
- `tack use` writes the `tack.yml` for you
- `tack doctor` names whatever is shadowing the shims dir on PATH (looking at you, nvm-windows)
- `tack doctor --fix` moves the shims dir to the front of the system PATH via a single UAC prompt, keeps `%SystemRoot%`-style tokens as tokens, and leaves a before/after backup in case you'd like your old PATH back
- `tack disable` / `tack enable` - an instant off switch with no PATH edits and no admin, for answering "is this tack's fault?"
- `tack update` updates tack in place from the latest release and restamps your shims with the new build; `tack changelog` tells you what you just got
- One-line PowerShell installer with fail-closed SHA-256 verification. No admin rights, anywhere, except the one you opt into
- Install and update wire the shims dir onto PATH and regenerate shims automatically - no manual reshim on a fresh box
- Reshims shrug off shims that are in use or being inspected by antivirus, instead of falling over mid-change
- The shim carries honest Win32 metadata, so a copied `node.exe` identifies as tack rather than an anonymous unsigned binary
- Isolated `(Dev)` data space for development builds, which can sit on PATH politely behind a real install - so hacking on tack never touches your real config, shims or PATH

---
