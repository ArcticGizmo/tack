# M8 status - the `tack tools` command group

> **Done: 2026-09-24.** Regrouped the registry commands and added interactive removal.

The registry verbs read better as a noun-first group than as three unrelated top-level commands. `tack tools
add` is clearer than `tack register` (it sets up the pairing with `remove` and `list`), so the three now live
under one branch.

## The change

| Was | Now |
| --- | --- |
| `tack register <tool@version> --path ...` | `tack tools add <tool@version> --path ...` |
| `tack list` / `tack ls` | `tack tools list` |
| _(nothing)_ | `tack tools remove [tool@version]` |

**Breaking (pre-1.0):** the old top-level `register` / `list` / `ls` names were removed outright, not kept as
aliases - deliberately, to keep the help output honest about the one way to do each thing.

## `tack tools remove`

Three ways in, picked by how much you specify:

- **`tack tools remove`** (no argument) - an interactive, scrolling multi-select of every registered
  `tool@version`. Space to toggle, enter to confirm; selecting nothing cancels. That prompt _is_ the
  confirmation, so there's no extra y/n.
- **`tack tools remove <tool>`** - if the tool has one version, it's removed directly; if it has several, the
  same picker opens filtered to just that tool's versions (it works like a filter into the list).
- **`tack tools remove <tool@version>`** - removes exactly that version, no prompt.

Non-interactive terminals (pipes, CI) can't show the picker, so those paths instead print the candidate
`tool@version`s and ask you to pass one explicitly, changing nothing (exit 0).

Removal cleans up after itself (all in the pure, unit-tested `Tack.Core.Config.ToolRegistry.Remove`):

- a tool left with no versions is dropped, along with its default;
- a default whose version was just removed is repointed to the highest remaining version;
- a central binding left pointing at a removed `tool@version` is **warned about, not deleted** (it's the
  user's rule to fix - via `tack bind` or the UI).

Then a reshim prunes the now-orphaned shims.

## `tack tools add` - PATH discovery

Finding the binDir by hand was the fiddly part of registering a tool, so `--path` is now optional. Omit it and
tack discovers the tool on PATH itself - the in-process equivalent of `where`:

- **`Tack.Core.Resolution.PathScan.FindOnPath`** walks the effective PATH in resolution order (machine entries,
  then user), probes the exec extensions via the existing `BinaryLocator`, dedupes by directory, and skips
  excluded dirs - the tack shims dir, the parked `shims_disabled`, and the install dir - so the shim is **never**
  offered as if it were the real tool.
- **One hit** is used automatically (with a `found on PATH:` line). **Several** open an interactive
  single-select of the full exe paths (so two dirs with the same leaf name stay distinguishable). **None** fails
  with guidance to pass `--path`.
- Non-interactive terminals with several hits print the candidates and ask for `--path` (exit 1).

**Environment references are expanded.** PATH entries are stored as `REG_EXPAND_SZ` and often hold references
like `%NVM_HOME%` or `%SystemRoot%\system32` (this dev box has both). .NET's
`GetEnvironmentVariable(.., Machine/User)` already expands them, but `PathScan` doesn't lean on that: each
entry is passed through an injectable `expand` (default `Environment.ExpandEnvironmentVariables`) before probing,
so a raw value still resolves and the tool is registered by its real directory (verified on this machine -
`%NVM_SYMLINK%` -> `C:\nvm4w\nodejs`). An undefined variable stays literal and simply matches nothing.

It's a native scan rather than shelling out to `where.exe`: it stays in-process (Core never spawns), is
AOT-friendly, unit-testable, and can exclude tack's own dirs cleanly. The version still comes from the
`tool@version` argument; only the path is discovered.

## Code map

| Piece | Where |
| --- | --- |
| Registry read/remove logic (pure) | `Tack.Core.Config.ToolRegistry` (`Entries`, `VersionsOf`, `Exists`, `Remove`) |
| PATH discovery (pure) | `Tack.Core.Resolution.PathScan.FindOnPath` (+ `PathMatch`) |
| Commands | `Tack.Cli.Commands.ToolsCommands` (`ToolsAddCommand`, `ToolsRemoveCommand`, `ToolsListCommand`) |
| Interactive pickers | `MultiSelectionPrompt` (remove) / `SelectionPrompt` (add), both with a non-interactive fallback |
| Wiring | `AddBranch("tools", ...)` in `Program.cs` |

## Tests

`ToolRegistryTests` covers entries/versions listing, case-insensitive matching, keeping vs dropping a tool,
default repointing, unknown-target no-ops, and the orphaned-binding warning. `PathScanTests` covers ordered
multi-dir discovery, excluding the shims dir, dedup of repeated PATH entries, exec-extension probing,
environment-reference expansion (and undefined refs left literal), and the nothing-found case. **15 new tests;
82 total, all green.** The interactive pickers themselves aren't
unit-tested (they need a real TTY); their non-interactive fallbacks were confirmed by hand.
