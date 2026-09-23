# Changelog

All notable changes to tack are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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
