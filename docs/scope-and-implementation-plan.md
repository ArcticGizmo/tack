# tack — scope & implementation plan

> **Status: draft / greenfield (2026-09-22).** The repo is empty. This document defines what we're
> building, the architecture, and a milestone plan. It borrows its distribution and project-layout
> patterns wholesale from the sibling `perch` project (`../perch`) — Avalonia + .NET 10, Velopack, a
> single-line PowerShell installer, and a tiny NativeAOT helper binary.

---

## 1. What tack is

**tack is a per-directory tool-version dispatcher for Windows.** You install multiple versions of a
tool (Node, Python, a CLI) yourself; tack makes sure that whenever *any* process calls `node`, it gets
the version that directory is supposed to use — including from IDEs and build tools you don't launch
from your shell (Visual Studio, Rider, a background test runner, a service).

It does this with the **shim model**: a single directory of tiny proxy executables sits on your PATH.
Anything that inherits that PATH — GUI, service, terminal alike — hits the shim first, and the shim
execs the correct real binary based on the caller's working directory.

### The two ideas tack keeps separate

A deliberate design stance, and the reason tack exists as its own thing rather than "just use mise":

1. **Installing a version** — obtaining `node 20` on disk somewhere.
2. **Dispatching to the right version at call time** — deciding, per directory, which installed
   version a bare `node` should resolve to.

Most managers weld these together. **tack v1 does only (2).** You bring your own installs (however you
got them — winget, an archive, another manager); tack registers where they live and handles dispatch.
Installation-as-a-feature is explicitly deferred (see Non-goals). This keeps the first version small
and honest about what the hard, interesting part actually is: the dispatch layer.

### The problem it solves that shell-activation managers don't

Shell-activation (`mise activate`, `nvm use`) mutates the *environment of a shell that ran a hook*.
Visual Studio never sourced your PowerShell profile, so it sees none of it. Shims are real files on a
static PATH entry, so they reach every process. That reach is the whole point of tack.

---

## 2. Goals & non-goals

### Goals (v1)

- **Per-directory dispatch that reaches processes tack didn't launch** (the IDE case). Shims on PATH.
- **`tack.yml` project files**, discovered by walking up from the current directory.
- **Centrally-managed bindings** so a directory can resolve to pinned versions *without* a `tack.yml`
  living in the repo — for projects where you can't or won't commit tack config (no business buy-in).
- **A rich `tack` CLI** (Spectre.Console) to administer bindings, register installs, and answer
  "what does `node` resolve to here and *why*" (`tack info`, `tack which`, `tack list`, `tack doctor`).
- **A first-class desktop UI** (`tack open` / `tack ui`, Avalonia) for the same, because inspecting
  resolution and PATH health is genuinely nicer visually.
- **Frictionless distribution**: Velopack installer + a `irm …/install.ps1 | iex` one-liner, mirroring
  perch. No admin rights; per-user install.

### Non-goals (v1) — deliberately deferred

- **Installing/building tool versions.** No downloading Node/Python. tack registers existing installs.
  (A future "backends" feature could add this; it's a separable concern by design.)
- **Shell-activation mode** (mutating env on `cd`). Shims only — that's what makes IDE support work.
  A convenience `tack activate` for shells could come later; it is not the mechanism.
- **macOS / Linux.** The Core is written platform-agnostic and the shim model ports cleanly (symlinks +
  `execve` are *easier* there), but v1 ships Windows only — that's where the IDE-shim pain is sharpest
  and it's the target environment.
- **Multi-binary edge cases beyond the common set.** v1 shims a version's declared executables
  (e.g. Node exposes `node`, `npm`, `npx`, `corepack`); exotic packaging is out of scope.
- **Team/remote sync of central config.** Central config is a local file in v1. Syncing it (a repo, a
  URL) is a later idea.

---

## 3. Architecture

### 3.1 Project layout (mirrors perch)

Multi-project solution, `tack.slnx`, projects under `src/`:

| Project | TFM | Role |
| --- | --- | --- |
| `src/Tack.Core/` | `net10.0` | The engine: config models, resolution, the tool registry, shim planning, PATH doctor. **No UI, no Spectre, no YAML-at-runtime dependency the shim can't use.** |
| `src/Tack.Cli/` | `net10.0` | The `tack` command (assembly/exe name **`tack`**). Console app, Spectre.Console. This is what users type. |
| `src/Tack.Shim/` | `net10.0` (NativeAOT) | **`tack-shim`** — the tiny proxy exe copied per shimmed tool. Same role/shape as perch's `perch-hook`: NativeAOT, minimal, fast cold start. |
| `src/Tack.App/` | `net10.0-windows` + `net10.0` | The Avalonia desktop UI (**`tack-ui`**, WinExe). Launched by `tack open`. |
| `tests/Tack.Tests/` | `net10.0` | xUnit over `Tack.Core`. Resolution precedence, walk-up, glob matching, shim planning. |

Root: `install.ps1`, `.github/workflows/release.yml`, `publish.bat`, `docs/`.

**Why three executables, not one.** Windows makes a console subsystem (`tack.exe`, wants a console for
Spectre output) and a windowed subsystem (`tack-ui.exe`, must not flash a console) genuinely different
exe kinds; forcing both into one head fights the OS. So:

- `tack.exe` — console, the command you type. `tack open` **launches** `tack-ui.exe`.
- `tack-ui.exe` — Avalonia WinExe; also the Start-Menu shortcut target (the clickable experience).
- `tack-shim.exe` — NativeAOT proxy, copied under each shimmed tool's name.

All three are published into one directory and packed together by Velopack.

### 3.2 On-disk layout (per user)

```
%LOCALAPPDATA%\tack\
  current\                     # Velopack-managed install (tack.exe, tack-ui.exe, tack-shim.exe)
  shims\                       # <-- this dir goes on PATH; one *.exe per exposed tool binary
    node.exe  npm.exe  npx.exe  python.exe  ...
  config.json                  # central config: registry + bindings + defaults (tool-managed, JSON)
  resolved.json                # compiled fast-lookup the shim reads (regenerated on config change)
  log\                         # optional shim/CLI diagnostics
```

Project files: `tack.yml` committed (or not) in a project tree, discovered by walk-up.

### 3.3 Config: two files, two formats, on purpose

- **`tack.yml` (project, human-authored, YAML).** Tiny and hand-written. Just intent:

  ```yaml
  tools:
    node: 20.11.0
    python: "3.12"
  ```

- **`config.json` (central, tool-managed, JSON).** Never hand-edited in the normal case — written by
  `tack` CLI / the UI. Holds three things:
  - **registry** — `tool@version → { binDir, exposes: [names] }`: where each *installed* version lives
    and which executables it provides. This is the "shim things tack didn't install" surface: you
    register an existing install here.
  - **bindings** — `directoryGlob → { tool: version, … }`: the **centrally-managed** rules that apply
    without a `tack.yml` in the repo.
  - **defaults** — global fallback versions.

**Why the format split matters (this is load-bearing for the shim).** The shim is NativeAOT, so it
can't drag in a reflection-based YAML parser (YamlDotNet is AOT-hostile). So:

- Central data the shim needs is **JSON**, read via `System.Text.Json` **source generators** (AOT-safe).
  It's compiled into `resolved.json` — a flat, already-resolved lookup — so the shim does one cheap read.
- `tack.yml` is deliberately a **strict, tiny, flat subset** so the shim can parse it with a small
  purpose-built reader (no YAML library) on the rare directories that have one. The full `tack` CLI
  uses a real YAML parser (YamlDotNet) for authoring/validation; **a test asserts both parsers agree**
  on the schema.

This gives the good UX ("drop a `tack.yml` and it just works, no `tack reshim` needed") without paying
YAML's AOT cost in the hot path.

---

## 4. Resolution model (the semantics)

When a shim for exposed binary `X` (owned by tool `T`) runs in directory `D`, tack resolves the version
of `T` by this precedence, **highest wins**:

1. **Env override** — `TACK_<TOOL>_VERSION` (e.g. `TACK_NODE_VERSION=18.19.0`). The escape hatch / CI pin.
2. **Nearest `tack.yml`** walking up from `D` to the drive root. Explicit project intent.
3. **Central binding** — the most specific matching `directoryGlob` in `config.json`. This is the
   "managed without a repo file" path: work under `C:\work\employer\**` resolves to their pinned
   versions even though nothing was committed to their repos.
4. **Central default** for `T`.
5. **Passthrough** — if nothing resolves, exec the *next* `X` on PATH after the shims dir (configurable;
   see below). tack stays invisible where it isn't configured.

**Precedence decision to confirm (see Open decisions):** default is `tack.yml` **beats** a central
binding — a committed project file is a stronger statement of intent than a machine-wide rule. An org
that wants to *enforce* a version regardless can mark a binding `enforce: true` to lift it above
`tack.yml`. v1 ships the `tack.yml`-wins default; `enforce` is a small later addition.

**No-resolution behaviour** is configurable, default = **passthrough**. Rationale: it matches the whole
philosophy ("you don't need tack.yml everywhere") — tack only *acts* where a rule exists, and elsewhere
a bare `node` behaves exactly as if tack weren't installed. The alternative (`error`) is available for
people who want tack to be authoritative. `tack info` always states which branch a given directory hits.

---

## 5. The shim (the crux)

This is the part that's easy to prototype and hard to finish. Detailed because it's where the real work
and the platform sharp edges are.

### 5.1 Generation

`tack reshim` (and any registry change via CLI/UI) regenerates `shims\`. For each registered version's
`exposes` list, it writes a **copy of `tack-shim.exe`** named after the exposed binary (`npm.exe`, etc.).
The shim identifies which tool it's proxying from its own filename (`argv[0]` basename). A small
`shims\index.json` maps exposed-name → owning tool so the shim knows `npm` belongs to tool `node`.

- Copy, not symlink: symlinks on Windows need privilege or dev mode; a copy of a small AOT exe is cheap
  and privilege-free. (macOS/Linux later: symlinks.)
- Stale shims (a registered version removed) are pruned by `reshim`; `tack doctor` flags them meanwhile.

### 5.2 What the shim does on invocation

1. Basename of `argv[0]` → exposed name → owning tool `T` (via `index.json`).
2. Determine `D` = current working directory.
3. Resolve version of `T` for `D` (section 4). Fast path: read the pre-compiled `resolved.json` for
   central data; walk up for `tack.yml` only if present (a handful of directory `stat`s; parse only on hit).
4. Look up the real binary path for `T@version` from the registry (`binDir` + the exposed name, resolving
   `.exe` / `.cmd` / `.bat` as needed).
5. **Exec it**: spawn as a child, forward all args and the three std streams, propagate exit code and
   Ctrl-C, then exit with the child's code.

### 5.3 Windows exec-proxy sharp edges (call these out now)

Windows has **no `execve`** — the shim can't *become* the target; it spawns a child and must faithfully
proxy. The 20% that makes a toy shim rot:

- **Exit code**: wait on the child, exit with its `ExitCode`. (Don't background it.)
- **Ctrl-C / console signals**: the child shares the console. The shim should install a console control
  handler that **ignores** Ctrl-C (return `TRUE`) so the child — not the shim — handles the interrupt,
  then `WaitForExit`. Getting this wrong either kills the wrapper before the child cleans up, or leaves
  orphans.
- **No new window / correct std handles**: inherit handles; do not create a new console; don't
  redirect-then-forget (a naive `ProcessStartInfo` with `UseShellExecute=false` and no redirection is
  the right base, streams inherited).
- **`.cmd`/`.bat` targets** (npm is `npm.cmd`): these must run through the command processor; account
  for cmd's own quoting/exit-code quirks. v1 can lean on `ProcessStartInfo` with `UseShellExecute=false`
  and the full path to the `.cmd`, which Windows resolves via the associated interpreter.
- **Startup latency**: this runs on *every* `node` call, so it must be quick. NativeAOT (no JIT warmup),
  source-gen JSON, no YAML lib, minimal allocation, single `resolved.json` read. Target: single-digit ms
  overhead. Measure it (a bench in `Tack.Tests` or a `tools/bench-shim.ps1`).

`Tack.Tests` should cover: exit-code propagation, Ctrl-C not killing the wrapper early, `.cmd` dispatch,
and resolution correctness — against a stub "tool" that just prints its own path/args and a chosen exit
code. (Perch proved out its installer against a loopback `HttpListener`; same spirit — test the real
mechanism, not a mock of it.)

### 5.4 PATH management

The shims dir must be on the **user** PATH and must **precede** other tool installs (nvm-windows, a
standalone Node, etc.). tack edits the user PATH (`HKCU\Environment`) and broadcasts
`WM_SETTINGCHANGE` — no admin needed. `tack doctor` verifies ordering and names whatever shadows the
shims. PATH mutation lives behind a `Tack.Core` interface (`IPathEnvironment`) with a Windows
implementation, mirroring perch's "every OS capability behind an interface" convention, so a mac/linux
head can implement the shell-profile equivalent later.

### 5.5 Multi-binary tools: siblings and global bins

A "tool" in tack is not one binary — it can own several, and they must all resolve to **the same version**
for a given directory. There are two kinds.

**Sibling binaries (ship inside the install).** `node` comes with `npm`, `npx`, `corepack` in the *same*
`binDir`. So a registration exposes a set, and resolution keys on the **owning tool**, never on the binary
name:

```
node@20.11 → { binDir, exposes: ["node","npm","npx","corepack"] }
```

`shims\index.json` maps each exposed name → its owning tool (`npm → node`), so when the `npm` shim runs it
resolves *node's* version for the cwd and execs that version's `npm.cmd`. This is what makes "node@20 here"
imply "npm@20 here" for free. (The M1 spike simplified this — each shim resolved its own name; the
owning-tool grouping is registry work in M2/M3.)

**Global-package bins (installed later, e.g. `npm i -g typescript` → `tsc`).** These are the interesting
case, and they hinge on where npm puts globals:

- By default on Windows npm's global **prefix is `%APPDATA%\npm`** — *not* inside the node install and
  **shared across every node version**. So globals are already on PATH but are **not** version-scoped (the
  single last-installed one wins). That shared global is the exact mess version managers exist to fix.
- The prefix is configurable, and **only if it is per-version** can globals be version-scoped.

So there are two regimes, and `tack info` / `tack doctor` must always state which one a node version is in:

- **Shared prefix (default):** globals resolve everywhere but aren't version-scoped. tack can still shim
  them (see the `.exe` bonus below) but points at the one shared set.
- **Per-version prefix:** tack enumerates that version's global bin dir at reshim and generates a shim per
  tool, each **owned by that node version**. Resolving `tsc` in a dir = resolve node for the dir → look in
  *that* version's global prefix. True per-directory globals — the thing users actually want.

The model generalizes: a registration owns a static `exposes` set **plus** an optional dynamic `globalBin`
dir whose contents are enumerated at reshim; everything in both sets is a shim owned by that version. The
same shape covers pip/`pipx`, gem, cargo, etc. — different per-tool data, one mechanism.

**`.exe` bonus.** Global npm bins are `.cmd`/`.ps1`/shell scripts, so they are found only by shells via
`PATHEXT`, never by a bare `CreateProcess("tsc")`. Shimming them as real `tsc.exe` makes them reachable by
non-shell callers *and* version-scoped — a genuine improvement over vanilla npm globals, not just parity.
(Local per-project `node_modules\.bin` tools are a third category npm/npx already resolve per-project; tack
does not touch those. tack owns the toolchain binaries + the global layer.)

**Two hard parts (why this is a later feature, not v1 core):** (a) getting per-version scoping requires a
per-version prefix, which nudges tack toward *configuring npm* — in tension with the "dispatch, don't
manage installs" stance; the safe default is *reflect* whatever prefix is configured and only scope when it
is already per-version. (b) Staleness — a brand-new global bin has no shim until the next reshim (the
classic rbenv/asdf `rehash` problem); see 5.6.

### 5.6 Auto-reshim: keeping globals in sync

The staleness papercut (globals installed after the last reshim are invisible) is the top complaint about
shim managers. tack can hide it — and doing so does **not** cross the non-goal line: tack *observing* that
your npm installed something and refreshing its mirror is not tack *installing* anything. Three mechanisms,
cheapest-to-reason-about last:

1. **Arg-sniffing.** The `npm` shim inspects argv and reshims after the child exits when it looks like a
   global install/uninstall. Targeted, but npm/pnpm/yarn/pip grammars are a swamp (`i -g`,
   `install --global`, `--location=global`, `npm_config_global`, `corepack enable`, ...) so it has false
   negatives and catches only changes made *through that shim*.
2. **Stamp / mtime poll-on-use (recommended).** A shim whose owning tool declares a `globalBin` cheaply
   checks "has that prefix changed since my last reshim?" (one dir stat vs a stored stamp) and reshims if
   so. **Mechanism-agnostic** (catches any change however it happened), no brittle CLI parsing, sub-ms
   hot-path cost, no daemon. It refreshes exactly when you next invoke the tool — which is when you'd
   notice.
3. **Background watcher.** A tack process watches the prefix dirs and reshims in real time. Robust but a
   daemon — scope creep for a tool that is otherwise just files on PATH.

**Architecture rule (all mechanisms):** keep the knowledge *out of the AOT shim* — it must stay tiny, fast
and generic, never learning npm syntax. The compiled `resolved.json` carries per-tool data (`globalBin`,
`reshimStamp`, and for option 1 optional `reshimIf` patterns); the shim does only the cheap check and, on a
hit, spawns `tack reshim` **after the child exits** (the new bin doesn't exist until then), then propagates
the child's exit code. The heavy, tool-specific logic lives in `Tack.Core`/the CLI. Reshim must be
idempotent and locked (concurrent installs in two shells).

**Sequencing:** auto-reshim only means something once 5.5's global-bin enumeration exists (nothing to sync
otherwise). Both are v1.x polish, cleanly separable from the M2 core, and gated behind a config toggle for
people who want deterministic, explicit reshims.

---

## 6. The `tack` CLI (Spectre.Console)

Console app, assembly name `tack`. Spectre.Console for tables, trees, prompts, spinners, colour.

### Commands (v1)

| Command | Does |
| --- | --- |
| `tack info [tool]` | The headline diagnostic. With a tool: resolved version **and the source** (which `tack.yml` / binding / default), plus the absolute binary it'd exec. With no tool: a Spectre table of every managed tool → resolved version → source, for the current directory. |
| `tack which <tool>` | Print just the absolute path the shim would exec (scriptable; like `mise which`). |
| `tack list` / `tack ls` | Registered tools and versions; marks which are shimmed and which resolve here. |
| `tack shims` | List generated shims + target dir; PATH-ordering health line. |
| `tack use <tool>@<ver>` | Write/update `tack.yml` in the current dir. Interactive (Spectre `SelectionPrompt`) when the version is omitted — pick from registered versions. |
| `tack register <tool>@<ver> --path <binDir> [--exposes a,b,c]` | Add an existing install to the central registry (the "dispatch to something tack didn't install" case). |
| `tack bind <dirGlob> <tool>@<ver> [--enforce]` | Add a central directory binding (managed without a repo `tack.yml`). |
| `tack reshim` | Regenerate shims + `resolved.json`. Spectre status spinner. |
| `tack doctor` | Diagnose: shims dir on PATH? ahead of shadowers (nvm-windows…)? stale shims? missing binDirs? Renders as a checklist. |
| `tack open` / `tack ui` | Launch `tack-ui.exe`. |

Spectre specifics to lean on: `Table`/`Tree` for `info` (render the resolution chain as a tree —
override → tack.yml → binding → default), `SelectionPrompt`/`MultiSelectionPrompt` for interactive
`use`/`register`, `Status`/`Progress` for `reshim`, `Panel` + `Markup` colour for `doctor` verdicts.
Bare `tack` with no args can drop into an interactive menu.

**Design rule:** every command is a thin shell over `Tack.Core`. The CLI formats; Core decides. The UI
calls the same Core. No resolution logic in either front-end.

---

## 7. The desktop UI (`tack open`)

Avalonia, `net10.0-windows` + `net10.0` heads like perch's app. A first-class experience because
inspecting *why* a directory resolves the way it does is much nicer visually than scrolling CLI output.

Screens (v1):

- **Dashboard** — managed tools with their currently-resolved version for a chosen directory context,
  and the source of each resolution.
- **Directory inspector** — pick a folder; see exactly what tack resolves there and the winning rule.
  This is the "why is Visual Studio using the wrong Node?" debugger, made obvious.
- **Registry editor** — register installs, set `binDir`, edit `exposes`.
- **Bindings editor** — the central `dirGlob → versions` rules (the no-repo-file managed set).
- **PATH doctor** — visualise PATH entries, highlight what shadows the shims dir, one-click fix ordering.
- **Shims panel** — view/regenerate; flag stale shims.

Shares `Tack.Core` with the CLI. Follows perch's App conventions (composition root/`PlatformServices`,
single reused window instances, IO off the UI thread then marshal back).

---

## 8. Distribution & install (lifted from perch)

The distribution story is a near-clone of perch's — reuse the design and, crucially, its hard-won
gotchas. See `../perch/docs/distribution-plan.md` and `../perch/install.ps1` for the reference.

- **Velopack** packs `tack.exe` + `tack-ui.exe` + `tack-shim.exe` from one publish dir. `vpk pack
  --packId Tack --mainExe tack-ui.exe` (Start-Menu shortcut → the UI). Per-user install to
  `%LOCALAPPDATA%\tack`, no admin.
- **First-run / post-update hook** (via `VelopackApp` bootstrap in each head, as perch does in
  `Program`): add the tack install dir **and** the shims dir to user PATH, generate shims for any
  registered tools, and offer first-run onboarding in the UI.
- **`install.ps1` one-liner**: `irm https://raw.githubusercontent.com/<owner>/tack/main/install.ps1 | iex`.
  Same shape as perch's: resolve a GitHub release, download `SHA256SUMS.txt` + `Tack-win-Setup.exe`,
  verify SHA-256 fail-closed (delete on mismatch), then run the installer and get out of the way so
  Velopack owns every subsequent update. Env pins: `TACK_VERSION`, `TACK_REPO`.
- **`release.yml`**: `v*`-tag triggered; build → `vpk pack` → generate `SHA256SUMS.txt` in the final
  release job over the exact flattened artifact set (so the manifest matches the bytes uploaded).

**Reuse these perch lessons verbatim — they are not optional:**

- **Release-pipeline scripts stay pure ASCII.** `install.ps1` ships with no BOM; Windows PowerShell 5.1
  decodes it as the system codepage, and a UTF-8 em dash becomes a curly quote that PowerShell treats as
  a **string delimiter**, silently mis-parsing the rest of the file. Use plain hyphens. A test asserts it.
- **Never `Start-Process -Wait` on the installer** — it waits for descendants and hangs forever on the
  app the Setup launches. Wait on the Setup process's own handle (`[Diagnostics.Process]::Start(...)` +
  `WaitForExit`).
- **`Invoke-WebRequest.Content` is a `byte[]`** for octet-stream on 5.1 — decode it explicitly before
  parsing the checksums, or every install fails the hash lookup.
- **`throw`, never `exit`** — the script runs via `iex` in the user's own shell.
- Port perch's `tools/test-install.ps1` (rename Perch→Tack) as the installer's regression suite.

Signing/WinGet follow the same later sequencing perch documents (Azure Trusted Signing or SignPath
Foundation, then WinGet). Out of scope for the first working version.

---

## 9. Milestones

Ordered so there's a usable thing early and the risky part (the shim) is proven before polish.

- **M0 — Skeleton.** Solution, five projects, CI build. `tack --version`. Velopack pack produces an
  installer that installs and puts `tack.exe` on PATH. No dispatch yet.
- **M1 — The shim, end to end (the real spike).** `Tack.Shim` (NativeAOT) proxies one hard-registered
  tool: correct binary, exit code, Ctrl-C, `.cmd` targets, measured startup overhead. Prove it against a
  stub tool in `Tack.Tests`, then against a real second Node install invoked from **Visual Studio** (the
  acceptance test that matters). *If this milestone is ugly, everything else is premature.*
- **M2 — Resolution + config.** `Tack.Core` resolver with full precedence, `tack.yml` walk-up, central
  `config.json` (registry + bindings + defaults), compiled `resolved.json`. Both parsers agree (tested).
- **M3 — CLI.** Spectre `tack info` / `which` / `list` / `use` / `register` / `bind` / `reshim` /
  `doctor` / `shims`. PATH management + doctor.
- **M4 — UI.** `tack open` → Avalonia: dashboard, directory inspector, registry/bindings editors, PATH
  doctor.
- **M5 — Distribution hardening.** `install.ps1` + `SHA256SUMS.txt` + `release.yml`, ported test-install
  suite, first-run PATH/shim wiring on install.
- **Later.** `enforce` bindings; shell-activation convenience; macOS/Linux heads; version *installation*
  backends; central-config sync; **dependent / global bins (5.5) and stamp-based auto-reshim (5.6)**;
  signing + WinGet.

---

## 10. Key risks & mitigations

- **Shim startup latency** (runs on every tool call). → NativeAOT, source-gen JSON, no YAML in the hot
  path, single compiled `resolved.json` read, benchmarked. This is why M1 comes first.
- **Windows exec-proxy correctness** (Ctrl-C, exit codes, `.cmd`). → Treat as the core spike; test the
  real mechanism against a stub tool, not a mock.
- **PATH ordering fights other managers** (nvm-windows shadowing the shims dir). → `tack doctor` detects
  and explains; installer places shims dir ahead; document the interaction.
- **YAML/JSON schema drift between the shim's mini-parser and the CLI's full parser.** → One schema, a
  cross-check test that feeds the same files to both and asserts identical resolution.
- **`tack.yml` appearing without `tack reshim`.** → The shim reads `tack.yml` live (mini-parser); only
  *central* config needs recompilation. Dropping a project file just works.
- **Resolution surprises** ("why this version?"). → `tack info` and the UI directory inspector make the
  winning rule explicit, always.

---

## 11. Open decisions (confirm before/while building)

1. **`tack.yml` vs central binding precedence.** Recommended default: `tack.yml` wins; `enforce: true`
   lifts a central binding above it. Confirm this matches how you want org-managed dirs to behave.
2. **No-resolution behaviour.** Recommended default: passthrough to next-on-PATH (tack invisible where
   unconfigured). Alternative: error. Per-tool or global setting?
3. **Repo owner / URL for `install.ps1`.** perch uses `raw.githubusercontent.com/<owner>/<repo>/main/…`.
   Confirm the GitHub owner so the one-liner URL and `TACK_REPO` default are right.
4. **`tack.yml` grammar bounds.** How much YAML do we allow in the file the shim must parse? Recommend a
   strict flat `tools:` map only; anything richer lives in central JSON.
5. **Exposed-binary discovery.** On `tack register`, auto-detect a version's executables (scan `binDir`)
   or require `--exposes`? Recommend auto-detect with an override flag.
6. **Naming of the three exes on disk** — `tack.exe`, `tack-ui.exe`, `tack-shim.exe` proposed. Fine?
7. **Per-version npm global prefix policy (5.5).** Reflect-only (scope globals just when the prefix is
   already per-version) vs opt-in configuring a per-version prefix. Recommend reflect-only for v1.x to stay
   on the "dispatch, don't manage" side.
8. **Auto-reshim trigger (5.6).** stamp/mtime poll-on-use (recommended) vs arg-sniffing vs background
   watcher; and whether it defaults on or off (recommend a config toggle, on once global-bins land).
9. **Global-bin precedence.** When a global-package bin name collides with a separately-registered tool
   (e.g. `yarn` as a node global *and* its own registration), which wins? `tack info` must explain it.

---

## Appendix — relationship to perch

tack reuses perch's *distribution and structure* patterns (Core/App split, NativeAOT helper à la
`perch-hook`, Velopack, `install.ps1` with its ASCII/`byte[]`/no-`-Wait` lessons, `release.yml` with
final-job checksums) but is a different kind of app: perch is a tray monitor; tack is a CLI-first
dispatcher with a companion UI. Where perch's helper (`perch-hook`) writes session sidecars, tack's
helper (`tack-shim`) proxies tool invocations — same architectural slot (small, fast, NativeAOT,
copied/wired onto the user's machine), different job.
