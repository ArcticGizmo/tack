# M2 status - resolution + config

> **Done: 2026-09-23.** The resolution engine and config model are in `Tack.Core`, the shim resolves through
> them, and the two tack.yml parsers are proven to agree. Reference: scope plan section 4 + milestone M2.

## What's in place

| Piece | Where | Notes |
| --- | --- | --- |
| Central config model (`config.json`) | `Tack.Core/Config/CentralConfig.cs` | registry (tool@version -> binDir + exposes), bindings (glob -> tools, `enforce`), defaults, settings. |
| Compiled config (`resolved.json`) | `Tack.Core/Config/ResolvedConfig.cs` | flat shim-facing form + source-gen JSON (`TackJson`, AOT-safe). |
| Compiler | `Tack.Core/Config/ConfigCompiler.cs` | builds the exposed-name -> owning-tool index, splits bindings per tool with a precomputed specificity. |
| Resolver (full precedence) | `Tack.Core/Resolution/Resolver.cs` | env override -> enforced binding -> nearest `tack.yml` (walk-up) -> binding -> default -> passthrough; records the winning source for `tack info`. |
| Glob matcher | `Tack.Core/Resolution/Glob.cs` | `*` (segment), `**` (any depth, trailing `/**` matches the base), case-insensitive; specificity ranking. |
| Version matching | `Tack.Core/Resolution/VersionMatch.cs` | exact, else highest dotted-prefix match (so `python: 3.12` -> `3.12.4`). |
| Binary locator | `Tack.Core/Resolution/BinaryLocator.cs` | probes `.exe`/`.cmd`/`.bat` in a version's binDir. |
| Mini `tack.yml` parser | `Tack.Core/Config/MiniTackYml.cs` | dependency-free, AOT-safe - the exact code the shim runs. |
| Full `tack.yml` parser | `Tack.Cli/TackYmlParser.cs` | YamlDotNet, authoring/validation only; kept out of the shim's AOT graph. |
| Shim = thin front-end | `Tack.Shim/Program.cs` | filename -> `Resolver` -> `BinaryLocator` -> exec, with passthrough-to-next-on-PATH. |

## Tested (38 tests total)

Glob matching + specificity; version prefix matching; mini-yaml parsing; compiler index/bindings; the full
precedence ladder (env / enforce / tack.yml / binding / default / passthrough, plus unregistered and
version-not-installed); the shim end-to-end on the real schema (tack.yml walk-up, env override, `.cmd` vs
native dispatch, stdin, passthrough); and the **mini vs full tack.yml parser agreement** guardrail.

## Sequencing note

`config.json` on disk is authored by the CLI (`register` / `bind` / `use`), and `resolved.json` is produced
by running the compiler at reshim time - both are **M3** (the CLI commands). M2 delivered the engine those
commands drive; the resolver, compiler, and both parsers are done and tested.

## Next: M3 - the CLI

Spectre.Console `tack info` / `which` / `list` / `use` / `register` / `bind` / `reshim` / `doctor` /
`shims`, PATH management surfaced through `doctor`, and `reshim` (compile `config.json` -> `resolved.json`
and generate the per-tool shim copies). Every command stays a thin shell over `Tack.Core`.
