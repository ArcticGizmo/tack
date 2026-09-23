# M6 status - changelog integration

> **Done: 2026-09-23.** A "what's new" story ported from perch, adapted to tack's CLI-first + MVVM-tab
> shape. Reference: perch's `ChangelogParser` / `ChangelogWindow` / `bump-version` skill.

Perch surfaces its changelog two ways: a Settings page and a post-update popup, both fed by an embedded
`CHANGELOG.md`. tack has no Settings window (it's tabbed and CLI-first), so the same capability lands as a
**What's New tab**, a **post-update popup**, and a **`tack changelog` CLI command** - one embedded file
behind all three.

## The one source of truth

`CHANGELOG.md` at the repo root, Keep-a-Changelog format (`## [Unreleased]`, then `## [vX.Y.Z] - date`
newest-first). It is **embedded at build time** into both heads:

- `Tack.Cli.csproj` and `Tack.App.csproj` each `<EmbeddedResource Include="..\..\CHANGELOG.md"
  LogicalName="Tack.CHANGELOG.md" />`.
- Nothing else references the file at runtime, so editing this one file updates every surface on the next
  build. The `bump-version` skill edits it; there is no generated copy to keep in sync.

## Core (shared, testable)

Kept in `Tack.Core/Changelog/`, pure and UI-free so both heads and the tests share one implementation:

| Type | Role |
| --- | --- |
| `ChangelogParser` | Splits the markdown into `ChangelogSection`s (heading + version + block). `UnseenSince(md, lastSeen, current)` returns the sections in `(lastSeen, current]`, newest-first; `Latest(md)` skips `[Unreleased]`. Ported verbatim from perch. |
| `ChangelogMarkup` | `StripInline` - reduces bold/italic/code/links to display text. Shared so the Spectre and Avalonia renderers strip identically. |
| `VersionInfo` | Reads an assembly's clean `X.Y.Z` (from `Directory.Build.props` via the informational-version attribute, `+sha` stripped) - the value the "what's new" logic diffs against. |
| `UiState` / `UiStateStore` | `ui-state.json` under `%LOCALAPPDATA%\tack` - `LastSeenVersion` + `ShowChangelogOnUpdate`. Its own source-gen JSON context (`UiStateJson`), kept out of the shim-facing config context. Save failures are swallowed - a UI preference is never worth a crash. New path: `TackPaths.UiStateJson`. |

## CLI - `tack changelog`

`Tack.Cli/Commands/ChangelogCommand.cs`. Latest release by default; `--all` for the full history,
`--number N` for the most recent N. Renders with Spectre (bold version + faint date + bulleted body) so it
reads like the rest of the CLI. `Program.cs`'s `ResolveVersion()` now delegates to `VersionInfo.Of`.

## UI - What's New tab + post-update popup

- **`ChangelogMarkdown`** (`Tack.App/Changelog/`) - loads the embedded file and renders the lightweight
  markdown into themed Avalonia controls using the app's shared style classes, so it matches every screen.
- **What's New tab** (`ChangelogView` + `ChangelogViewModel`, nav index 6) - the full changelog, newest
  first. Content-only; `Refresh()` is a no-op because the changelog is baked into the build.
- **Post-update popup** (`ChangelogWindow`) - shown once when the running version differs from
  `LastSeenVersion`. Wired in `App.axaml.cs`: `ResolvePendingChangelog()` (guarded by
  `ShowChangelogOnUpdate`, a non-null prior version, and a version change) picks the unseen sections, then
  `StampSeenVersion()` records the current version so it fires at most once per update. The popup's "Don't
  show changelogs again" flips `ShowChangelogOnUpdate` off. A fresh install seeds the version silently -
  nothing to pop.

## The `bump-version` skill

`.claude/skills/bump-version/SKILL.md`, adapted from perch's. Derives the next version from the **last git
tag** (not `Directory.Build.props`, which drifts), bumps `<Version>` in `Directory.Build.props` (the single
value every project inherits), backfills any tagged-but-undocumented versions, writes a new dated section,
clears `[Unreleased]`, and keeps the deadpan tone. It edits only those two files and reports - tagging stays
with `publish.bat` / `release.yml`.

## Tests

`tests/Tack.Tests/ChangelogTests.cs` - parsing/section-trimming, `UnseenSince` range + fresh-install/same-
version empties, `Latest`, `StripInline`, and a `UiStateStore` round-trip. **7 new tests; 57 total, all
green.**

## Notes / follow-ups

- The popup only fires on a real version change, so you won't see it in day-to-day dev unless you hand-edit
  `ui-state.json`'s `lastSeenVersion` to an older value. (Perch has a `#if DEBUG` preview command; tack
  could add one if it becomes useful.)
- `v0.1.0` was written from the M0-M5 milestone outcomes (the git history is squashed), at the "general
  outcomes" altitude - not a commit-by-commit trail.
