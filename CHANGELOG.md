# Changelog

All notable changes to tack are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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
- `--enforce` zones beat a repo's `tack.yml`, for when the machine's rule has to win
- `none` zones switch tack off for a tool (`node@none`) or for every tool (`none`) in a directory tree, so commands fall through to whatever is next on PATH
- Resolution precedence you can actually explain: env override (`TACK_<TOOL>_VERSION`), enforced zone, `tack.yml`, zone, default, then passthrough
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
