# M3 status - the CLI

> **Done: 2026-09-23.** The `tack` command surface (Spectre.Console) over the M2 engine, plus the Core
> maintenance layer it drives. Reference: scope plan section 6 + milestone M3.

## Commands (all working, smoke-tested end to end)

| Command | Does |
| --- | --- |
| `tack info [tool]` | Resolved version + source for the cwd. A tool arg renders the resolution as a tree (version, winning rule, the exact binary); no arg renders a table of every tool. |
| `tack which <tool>` | Prints just the absolute path the shim would exec (plain, scriptable). |
| `tack list` / `ls` | Registered tools, versions, default, and what resolves here. |
| `tack shims` | Generated shims + the shims dir + a PATH-health line. |
| `tack doctor` | Checklist: shims dir exists / on PATH / shadowed by another install / stale shims / missing binDirs. |
| `tack register <tool@ver> --path <binDir> [--exposes a,b,c]` | Adds an install to the registry (auto-detects exposes), sets the first version as default, and reshims. |
| `tack bind <glob> <tool@ver> [--enforce]` | Adds a central directory binding and reshims. |
| `tack use <tool[@ver]>` | Writes/updates `tack.yml` in the cwd (version defaults to the registered default/highest). |
| `tack reshim` | Recompiles `config.json` -> `resolved.json` and stamps/prunes shim copies. |
| `tack open` / `ui` | Launches `tack-ui`. |
| `tack --version` | `0.1.0`. |

## Core maintenance layer (Tack.Core, the engine the commands drive)

- `ConfigStore` - load/save `config.json` (missing = empty).
- `Reshimmer` - compile `config.json` -> `resolved.json`, stamp one shim copy per exposed name, prune stale.
- `ToolProbe` - auto-detect a binDir's exposed executables.
- `PathDoctor` - PATH health (on-path, shadowing, stale shims, missing binDirs), FS/env injected for tests.

Every command stays a thin shell: Core decides, the CLI formats. 47 tests total (9 new for the maintenance
layer; the resolver/parser/shim suites from M2 unchanged).

## Notes / follow-ups

- **Dev vs installed shim stamping.** `reshim` copies `tack-shim.exe` from beside `tack.exe`. Under a dev
  `dotnet run` those aren't co-located, so reshim writes `resolved.json` but reports "no shims stamped" -
  expected. In a Velopack install they sit together and shims are generated.
- **PATH mutation** stays at install time (the Velopack callback via `WindowsPathInstaller`); `doctor` is
  read-only. A `tack doctor --fix` / `tack path` could add the CLI-side repair later (deliberately omitted
  here to avoid an EDR-sensitive PATH write on the dev box).
- **Interactive `use`** (Spectre `SelectionPrompt` when the version is omitted) is a small future nicety;
  today `use` falls back to the registered default/highest.

## Next: M4 - the desktop UI

Flesh out `tack-ui` (Avalonia) beyond the M0 placeholder: dashboard, the directory inspector ("why is VS
using the wrong node?"), registry + bindings editors, PATH doctor, and the shims panel - all over the same
`Tack.Core`.
