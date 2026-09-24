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

## Code map

| Piece | Where |
| --- | --- |
| Registry read/remove logic (pure) | `Tack.Core.Config.ToolRegistry` (`Entries`, `VersionsOf`, `Exists`, `Remove`) |
| Commands | `Tack.Cli.Commands.ToolsCommands` (`ToolsAddCommand`, `ToolsRemoveCommand`, `ToolsListCommand`) |
| Interactive picker | `MultiSelectionPrompt<string>` (PageSize 15, wrap-around) with a non-interactive fallback |
| Wiring | `AddBranch("tools", ...)` in `Program.cs` |

## Tests

`ToolRegistryTests` covers entries/versions listing, case-insensitive matching, keeping vs dropping a tool,
default repointing, unknown-target no-ops, and the orphaned-binding warning. **8 new tests; 75 total, all
green.** The interactive picker itself isn't unit-tested (it needs a real TTY); the non-interactive fallback
was confirmed by hand.
