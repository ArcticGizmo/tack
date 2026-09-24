# Changelog

All notable changes to tack are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- `tack disable` / `tack enable` - a one-word off switch. Disable parks the shims dir as `shims_disabled` so the PATH entry resolves to nothing and every tool falls straight through to the real PATH; enable renames it back. No PATH edits, no admin, and even already-open shells stop hitting the shims immediately. Handy for A/B-ing "with tack vs without", and for dev testing.
- Configuration keeps working while disabled: `register`, `bind` and `reshim` write into the parked dir, so whatever you set up meanwhile goes live the moment you `tack enable`.
- `tack doctor --fix` - stop reading the diagnosis and just fix it. Regenerates shims, then promotes the shims dir to the **front of the system (machine) PATH** so it beats system-wide installs a user-PATH entry can never outrank. Prompts for elevation via UAC when it needs it (and only for that one write).

- `tack tools` command group - `add`, `remove`, `list`. `tools remove` with no version drops you into an interactive, scrolling multi-select of the registered versions (give it a tool name to filter the picker to just that tool's versions; give a full `tool@version` to remove it outright). Removing a version tidies up after itself: it drops a tool left with nothing, repoints a default whose version just vanished, and warns about any binding left pointing at the removed version.

### Changed

- `tack doctor` and `tack shims` now report the disabled state plainly instead of crying FAIL about a shims dir that's off PATH on purpose.
- Registry commands are now grouped under `tack tools`. **Breaking (pre-1.0):** `tack register` is now `tack tools add`, and `tack list` / `tack ls` are now `tack tools list`. The old top-level names are gone.

---

## [v0.1.0] - 2026-09-23

The first working tack: per-directory tool dispatch that reaches the processes shell hooks can't.

- Per-directory tool dispatch via shims on PATH - a bare `node`/`python` resolves to the version the directory is supposed to use
- Reaches processes you didn't launch from a shell (Visual Studio, Rider, background runners) - the whole reason tack exists
- `tack.yml` project files, discovered by walking up from the current directory
- Central directory bindings, so a repo can be pinned without committing a `tack.yml` to it
- Resolution precedence you can actually explain: env override, `tack.yml`, central binding, default, then passthrough
- Passthrough when nothing matches - tack stays invisible where it isn't configured
- The `tack` CLI: `info`, `which`, `list`, `use`, `register`, `bind`, `reshim`, `doctor`, `shims`, `open`
- `tack doctor` names whatever is shadowing the shims dir on PATH (looking at you, nvm-windows)
- A desktop UI (`tack open`): dashboard, directory inspector, registry and bindings editors, PATH doctor, shims panel
- Nord (Dark) throughout the UI, because inspecting why a directory resolves the way it does should at least be pleasant
- One-line PowerShell installer with fail-closed SHA-256 verification, and Velopack self-updates thereafter
- Install and update wire the shims dir onto PATH and regenerate shims automatically - no manual reshim on a fresh box
- The shim carries honest Win32 metadata, so a copied `node.exe` identifies as tack rather than an anonymous unsigned binary
- Isolated `(Dev)` data space for development builds, so hacking on tack never touches your real config, shims or PATH

---
