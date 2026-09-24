# Changelog

All notable changes to tack are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- `tack disable` / `tack enable` - a one-word off switch. Disable parks the shims dir as `shims_disabled` so the PATH entry resolves to nothing and every tool falls straight through to the real PATH; enable renames it back. No PATH edits, no admin, and even already-open shells stop hitting the shims immediately. Handy for A/B-ing "with tack vs without", and for dev testing.
- Configuration keeps working while disabled: `tools add`, `zones add` and `reshim` write into the parked dir, so whatever you set up meanwhile goes live the moment you `tack enable`.
- `tack doctor --fix` - stop reading the diagnosis and just fix it. Regenerates shims, then promotes the shims dir to the **front of the system (machine) PATH** so it beats system-wide installs a user-PATH entry can never outrank. Prompts for elevation via UAC when it needs it (and only for that one write). The rewrite goes through the registry directly and **preserves environment tokens** like `%SystemRoot%\system32` and `%NVM_HOME%` (keeping the value's `REG_EXPAND_SZ` type) instead of baking them into literal paths, and prints the full before/after plus writes a timestamped backup under the tack data dir so a bad edit can be reverted by hand. It only touches the system PATH - the user PATH is left alone.

- A dev build (`run.bat`) can now be run like a real install, just politely behind it. `tack doctor --fix` under the dev profile puts the dev shims dir at the **front of the system PATH, directly behind the release tack's shims**. That way it beats every real install but never the release tack. The same UAC prompt, token preservation and backup apply, and like the release fix it never touches the user PATH. Run the release `tack disable` to hand shared commands like `node` over to dev, and `tack enable` to take them back. Debug builds now ship a shim next to `tack`, so the dev reshim actually has something to stamp out.
- `tack tools` command group - `add`, `remove`, `list`. `tools remove` with no version drops you into an interactive, scrolling multi-select of the registered versions (give it a tool name to filter the picker to just that tool's versions; give a full `tool@version` to remove it outright). Removing a version tidies up after itself: it drops a tool left with nothing, repoints a default whose version just vanished, and warns about any zone left pointing at the removed version.
- `tack zones` command group - `add`, `remove`, `list`. A zone is a directory, plus everything under it, that uses a tool version without a repo `tack.yml`. Unlike `bind`, zones can be taken back: `zones remove <dir> [tool]`, or with no arguments an interactive picker. Adding a zone that already exists for that directory and tool updates it, so two zones can never argue about the same place.
- `tack tools add` can now find the binDir for you: omit `--path` and it discovers the tool on PATH (the in-process equivalent of `where`), skips tack's own shims dir, and - when there's more than one hit - lets you pick which install to register. One hit is used automatically; none tells you to pass `--path`. PATH entries that use environment references (`%NVM_HOME%`, `%SystemRoot%\system32`) are expanded before probing, so an nvm-style install is found and registered by its real directory.

### Changed

- `tack doctor` now reports the disabled state plainly instead of crying FAIL about a shims dir that's off PATH on purpose.
- Registry commands are now grouped under `tack tools`. **Breaking (pre-1.0):** `tack register` is now `tack tools add`, and `tack list` / `tack ls` are now `tack tools list`. The old top-level names are gone.
- Shims are plumbing, and now they're treated like it. **Breaking (pre-1.0):** `tack shims` is gone - `tack doctor` already covered everything it said about PATH health (and more), and the one useful bit, which command names tack intercepts, is now a `commands` column in `tack tools list`. `tack reshim` still works but is hidden from `--help`; every command that changes config already reshims, and so does `tack doctor --fix`, so you only need it after hand-editing `config.json`.
- Shims that don't resolve a command now fall through only to PATH entries *after* their own, which is what Windows would have picked next. Previously they searched from the top and skipped only themselves, so two tack instances on PATH would pass a command back and forth forever. Shims also find `resolved.json` in the data folder they were copied into, which ties each shim to its own profile. `tack tools add` discovery skips every tack instance's shims, and `tack doctor` reports being behind another tack as a hand-over hint instead of listing each shared command as shadowed.
- The desktop UI loses its Shims tab for the same reason. The "Regenerate shims" button moved to the PATH doctor page, next to the stale-shim warnings it fixes; the Registry page already listed each version's exposed commands.
- Central bindings are now **zones**, and they take a plain directory instead of a glob. **Breaking (pre-1.0):** `tack bind` is gone; use `tack zones add <dir> tool@version [--enforce]`. A zone covers its directory and everything below it, and the deepest zone wins. Every zone that applies is one of the current directory's parents, and parents nest, so there's always exactly one winner. Before, `C:/work/*/api/**` and `C:/work/**` tied on "specificity", and whichever came first in `config.json` quietly won. Matching is also a plain walk up the tree instead of building a regex per binding on every shim call. Existing configs migrate on load: `X/**` and a bare `X` both become a zone at `X` (so a bare path now covers its children, which is what it looked like it did all along). A glob with a wildcard mid-path has no single-directory equivalent. It's left in `config.json`, ignored, and named by `tack doctor` and every reshim until you replace it. The desktop UI's Bindings tab is now Zones.

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
