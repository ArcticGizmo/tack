---
name: bump-version
description: Bump the tack version and refresh the changelog. Determines the next version from the LAST GIT TAG (not the Directory.Build.props value, which can drift), bumps <Version> in Directory.Build.props, backfills changelog sections for any tagged versions that were never documented, summarises every change since the last tag into a new CHANGELOG.md section (keeping the dry humour), clears the Unreleased section, and prints the new version plus its changelog entries. Use when the user says "bump version", "bump the version", "/bump-version", or wants to cut a new release entry.
---

# Bump Version

Bumps tack to the next patch version and brings `CHANGELOG.md` up to date.

`CHANGELOG.md` is embedded into the CLI (`tack changelog`) at build time, so editing this one file is all it
takes. There is nothing else to regenerate - do not hand-edit anything under `src/`.

## Why the tag, not Directory.Build.props

`Directory.Build.props` carries `<Version>` as the documented single source of truth that `publish.bat` /
`release.yml` read - but it and the `[Unreleased]` changelog section both drift (a version can get tagged
without either catching up). The **last git tag is the real source of truth**. Always derive the next
version from it.

## Steps

### 1. Gather everything in one batch

Run this single block first - it collects the tag, the next version, the new commits, and the dates for
any backfill, so you don't make repeated round-trips:

```bash
LAST_TAG=$(git tag --sort=-v:refname | head -1)
echo "LAST_TAG=$LAST_TAG"
echo "=== all tags (oldest->newest) ==="; git tag --sort=v:refname
echo "=== new commits on HEAD since $LAST_TAG ==="
git log $LAST_TAG..HEAD --no-merges --date=short --pretty=format:'%h %ad %s'
```

Parse `LAST_TAG` as `vMAJOR.MINOR.PATCH`. The next version is the same with **PATCH + 1**
(e.g. `v0.1.0` -> `v0.1.1`). Call it `$NEXT` (Directory.Build.props wants it without the `v`, e.g. `0.1.1`).
If there are no tags at all, fall back to the `Directory.Build.props` `<Version>` and bump that, and say so.

**Scope is `HEAD`, deliberately.** Only commits reachable from `HEAD` are releasing. Do **not** use `--all`
- it drags in unmerged worktree/branch WIP that isn't shipping. If you know specific work landed on another
branch that *is* part of this release, add that branch explicitly; otherwise stay on `HEAD`.

Most commit subjects are self-explanatory - summarise straight from them. Only `git show <hash>` a commit
when its subject is genuinely opaque (e.g. "tweaks", "first pass"). Skip noise entirely: stash entries,
pure-WIP commits, and plumbing a user would never notice. Collapse related commits into the single *net*
change they add up to (see "What to write" in step 4).

### 2. Bump Directory.Build.props

Edit the `<Version>` element in `Directory.Build.props` to `$NEXT` (no `v` prefix). Leave everything else
untouched - set it to `$NEXT` regardless of its current value. Every project inherits this one value, so
there is nothing else to bump.

### 3. Backfill any missing tagged versions

A version can be tagged without ever getting a changelog section. From the tag list in step 1, find tags
with **no** matching `## [v...]` heading in `CHANGELOG.md`. For each missing tag (oldest first), get its
commits and date in one call, scoped to that tag's range:

```bash
# fill in <prev_tag> and <tag>; --date=short on the last line gives the heading date
git log <prev_tag>..<tag> --no-merges --date=short --pretty=format:'%h %ad %s'; \
  git log -1 --format='HEADING_DATE=%cd' --date=short <tag>
```

Insert each backfilled `## [v<tag>] - YYYY-MM-DD` (using its own tag date, not today) in the correct
reverse-chronological slot, applying the same summarising and dry-humour rules below. Do every missing tag,
not just the latest.

### 4. Rewrite the changelog

In `CHANGELOG.md`:

- **Create a new section** directly under `## [Unreleased]`, titled `## [v$NEXT] - YYYY-MM-DD` using
  today's date.
- Fill it with the summarised, user-facing bullets from step 1. **Fold in** anything already sitting in the
  `[Unreleased]` section - those changes are part of this release.
- **Clear the `[Unreleased]` section** so it sits empty (keep the `## [Unreleased]` heading and the `---`
  separators; just remove its bullets).
- Preserve the existing file format exactly: heading style, the `---` separators, and the
  reverse-chronological order (newest version on top).

#### What to write

- **Write for the end user, not the developer.** Describe what changed for someone *using* tack. Mention
  implementation details only when they genuinely matter to the user (e.g. "existing config carries over").
  Internal refactors, file moves, and plumbing don't get a bullet at all.
- **Keep bullets snappy - this is the rule that gets ignored most, so enforce it hard.** Aim for a short
  phrase, roughly ten words or fewer; one line, never two. Cut "now", "the ability to", "you can now". Drop
  the trailing "so you can..." rationale - the change speaks for itself; keep an aside only when it earns
  the dry humour. After drafting, reread every bullet and shorten any that runs long.
- **Describe the cumulative change, not the commit trail.** A version's section is the net difference from
  the previous version - the end state, not the journey. If a feature was added, then fixed, then tweaked
  across several commits, that's *one* bullet describing the finished feature.

#### Tone - keep the dry humour

The changelog has a deadpan, self-aware voice. Match it - look at the `v0.1.0` entries for the register,
e.g.:

> - tack doctor names whatever is shadowing the shims dir on PATH (looking at you, nvm-windows)
> - The shim carries honest Win32 metadata, so a copied node.exe identifies as tack rather than an anonymous unsigned binary

Write straight bullets for ordinary changes; reserve the parenthetical asides and dry understatement for the
moments that earn them. Don't force a joke into every line - the humour lands because most lines are plain.
Never invent changes that didn't happen for the sake of a punchline.

### 5. Report back

Print, plainly:

- The next version number (e.g. `v0.1.1`).
- The exact changelog entries you wrote for that version.
- If you backfilled any missing tagged versions, list which ones and show their entries too.

## Notes

- This skill does **not** commit, tag, or push - it only edits the two files (`Directory.Build.props` and
  `CHANGELOG.md`) and reports. Tagging happens separately (see `publish.bat` / `release.yml`, which read the
  version from `Directory.Build.props`).
- The changelog is embedded at build time, so nothing under `src/` needs touching - a rebuild picks up the
  new content automatically for `tack changelog`.
- Only the patch component is bumped. If the user wants a minor/major bump, they'll say so - follow their
  instruction instead of auto-incrementing patch.
