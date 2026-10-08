# Plan: managed installs (M10)

*Drafted 2026-10-02 on `feature/managed-installs`.*

tack has always said "you install the versions, tack does the dispatch" ([scope plan §2](scope-and-implementation-plan.md#non-goals-v1--deliberately-deferred):
*"No downloading Node/Python. tack registers existing installs."*). That plan already left room for this as a
separable "backends" feature, and this is that feature. nvm-windows and fnm are the friction this removes: tack
already does their dispatch better, so the only reason left to keep them is that they fetch versions.

**Opt-in and additive.** `tack tool add` is unchanged and still takes an install from anywhere. `tack tool install`
is a convenience on top: download, check, unpack into tack's own folder, then register it exactly as `tool add`
would. Nothing ever downloads unless you run an install or availability command.

First cut: **Node and Python**, Windows on the host's native architecture. tack is still greenfield, so breaking
changes are fine.

## What it looks like

```powershell
tack tool available node            # newest of each major, LTS marked
tack tool available python 3.12     # every 3.12.x
tack tool install node@20           # newest 20.x -> registers node@20.11.1
tack tool install node@lts          # newest LTS
tack tool install python@latest     # newest stable
tack tool list                      # managed versions are marked
tack tool remove node@20.11.1       # unregisters it and deletes its files (--keep-files to keep them)
```

## Target layout

| What | Where |
|---|---|
| one managed version | `%LOCALAPPDATA%\tack\installs\<tool>\<version>\` (for example `installs\node\20.11.1\node.exe`) |
| in-progress downloads | `%LOCALAPPDATA%\tack\installs\.staging\<random>\` |
| removed, awaiting delete | `%LOCALAPPDATA%\tack\installs\.trash\<random>\` |
| cached version indexes | `%LOCALAPPDATA%\tack\installs\.cache\<tool>-index.json` |
| dev profile | the same, under `%LOCALAPPDATA%\tack (Dev)\installs\` |

**LocalAppData, not Roaming.** These folders are hundreds of MB, specific to the machine's architecture, and must
never sync with a roaming profile. Velopack updates only replace `current\`, so installs survive updates.
Uninstalling tack keeps them along with the rest of tack's data, as it does today.

## Decisions taken (overrule any)

| # | Decision | Instead of |
|---|---|---|
| I1 | **The shim never touches the network, and a `tack.yml` never causes a download.** Installing is always an explicit command you type. | auto-installing a missing pinned version on first call |
| I2 | **Register the exact version.** `node@20` and `node@lts` are resolved when you install; the registry gets `node@20.11.1`. Pins keep matching by dotted prefix, so `node: 20` in a `tack.yml` still picks it. | registering under the spec you typed |
| I3 | **Fail closed on integrity.** HTTPS only (and no redirect down to HTTP). The archive's SHA-256 must match the vendor's published value (Node's `SHASUMS256.txt`, python.org's index), otherwise the download is deleted and nothing is registered. Checking signatures (Node's GPG, Python's Sigstore) comes later. As with `install.ps1`, a hash from the same host proves the bytes weren't damaged on the way, not who made them. | trusting TLS alone |
| I4 | **The source declares what a version exposes,** filtered to the files that actually exist. A folder scan would pick up Node's `install_tools.bat` and `nodevars.bat`. Node: `node npm npx corepack` (corepack is gone by Node 26, so it's dropped there). Python: `python` plus `pip pip3` from `Scripts\` (I17). **Not `pythonw`:** the shim is a console program, so a `pythonw` shim would open the console window that `pythonw` exists to avoid (found in checkpoint 2). | `ToolProbe.DetectExposes` |
| I5 | **A version can have extra bin folders.** `InstalledVersion.ExtraBinDirs` (null when empty, so plain versions never write the key) are searched after `BinDir`, by the shim and by `which`. That's how Python's `Scripts\pip.exe` is reached. | renaming `BinDir` to a list everywhere |
| I6 | **Native architecture only.** x64 Windows gets x64 builds, arm64 gets arm64. If the vendor has no build for this architecture, the install fails and says so; there's no quiet fallback to emulated x64. | falling back to x64 on arm64 |
| I7 | **tack only deletes what it installed.** A managed version carries an `Install` receipt in config (source, URL, SHA-256, date) **and** a `.tack-install.json` file in its folder. tack deletes a folder only if it is strictly inside the installs root and has that file. Everything registered with `tool add` is never deleted. | trusting the config flag alone |
| I8 | **Installing a version name that's already registered from elsewhere is refused.** If `node@20.11.1` already points at `C:\node\20.11.1`, `tool install node@20.11.1` says so and stops. Remove it first. Installing a version that's already managed is a no-op that says so. | silently replacing it |
| I9 | **Removing a managed version deletes its files by default** (`--keep-files` keeps them, and deletes the marker so tack never deletes them later). Two checks make an in-use version fail cleanly before the config changes. First, no program may be running from the folder, and the error names it: `node.exe (pid 1234)`. That check was added in checkpoint 5, which found that **Windows lets a folder be renamed while an exe in it is running**. Second, the folder is renamed into `.trash\` before anything is deleted, which Windows refuses while a file in it is open or a terminal's current directory is inside it. After the rename, tack unregisters it and deletes the trash; anything it can't delete yet is retried on the next install or `doctor --fix`. | deleting in place and leaving half a folder |
| I10 | **Installs are atomic.** Download and unpack into `.staging\` and check the result. Then rename it into place (on the same volume, so the rename is atomic), run the post-install step **there** (pip's launchers embed their absolute path, so they can't be made in staging; see the [findings](m10-spike-findings.md#theres-no-pipexe)), and only then register it. If the post-install step fails, the folder goes to `.trash\` and nothing is registered. An interrupted install never leaves a registered half-install. Stale staging folders are swept on the next install. One mutex per profile serialises installs. A managed version is never moved after it's placed. | unpacking in place |
| I11 | **Unpack the whole archive, then move its top folder.** `ZipFile.ExtractToDirectory` already refuses entries that escape the destination (no zip slip; a test pins that). The source names the archive's top folder (`node-v20.11.1-win-x64\`, or none for Python's flat zip), and that folder is what gets renamed into place, so tack never extracts entry by entry. | per-entry extraction with a stripped prefix |
| I12 | **Python is the python.org build:** the hashed zips listed in its install-manager index (`index-windows.json` and its `next` pages), `pythoncore-*` entries only. That puts the **floor at 3.11.0**. Older versions are only on NuGet with no hash in the index, and asking for one says so. Never the embeddable zip (`pythonembed-*`): its `._pth` file switches off `site` and it has no pip. No `py` launcher, no `python3` alias, and no PEP 514 registry entries. tack still writes nothing but its own user PATH entries. | python-build-standalone; NuGet packages for 3.10 and older |
| I13 | **Pre-releases only when named exactly.** `python@latest` and `python@3.15` skip `3.15.0rc2`; `python@3.15.0rc2` installs it. Free-threaded builds (`3.13t`) are out of the first cut. Python has no LTS, so `python@lts` is an error that says to use `latest` or a version. A version with no Windows build (Python's source-only security releases, such as 3.12.12) doesn't exist as far as tack is concerned: `python@3.12` gets 3.12.10. | including pre-releases |
| I14 | **Versions are ordered numerically, not as strings.** One shared `VersionOrder` in Core does "newest" for aliases, `available` and the default repointing in `ToolRegistry.Remove`, which today orders by string and so picks `9.0` over `10.0`. | string order |
| I15 | **Network calls use the system proxy** (`HttpClient`'s default on Windows). No mirror setting in the first cut. **Certificate revocation isn't checked** (also `HttpClient`'s default). A TLS-inspecting gateway's certificate often can't be checked, and Windows clients that insist, like `curl.exe`, fail outright behind one (found after checkpoint 6). | a `TACK_NODE_MIRROR`-style setting now; revocation checking |
| I16 | **The first version installed becomes the default,** the same rule as `tool add`. Both paths go through one Core registration function, so they can't drift. | separate rules for install |
| I17 | **Python's post-install step creates pip's launchers offline:** `python -I -m pip --isolated install --force-reinstall --no-index --no-deps --find-links Lib\ensurepip\_bundled pip`, run in the final folder (I10). `-I` and `--isolated` keep your `PYTHON*` and `PIP_*` variables and pip config out of it (`PIP_REQUIRE_VIRTUALENV` would otherwise refuse). The spike ran it without those two flags, so checkpoint 4's first real install confirms them. The zip ships pip in `site-packages` but no `Scripts\`, and `ensurepip --upgrade` does nothing when pip is already there. | exposing no `pip` (only `python -m pip`) |

## Checkpoints

Each checkpoint is one commit (or a small run of them) that builds, passes the tests and can be reviewed alone,
and ends with an explicit **Done when**. Unit tests never use the network: sources parse recorded fixtures and the
installer runs against a fake downloader. Live downloads are an opt-in test category (`TACK_LIVE_TESTS=1`).

### Checkpoint 0: spike the sources (no product code) ✅ 2026-10-02

Done: see [the findings](m10-spike-findings.md). The fixtures are in `tests/Tack.Tests/Fixtures/Installs/`, and
I10 to I13 and I17 are amended above. Running from `%LOCALAPPDATA%\tack\installs\` itself carries over to
checkpoint 4.

A throwaway console under `spikes/m10-installs/`. Findings go in `docs/m10-spike-findings.md`.

- **Node:** the shape of `https://nodejs.org/dist/index.json` (the `lts` field, the `files` keys `win-x64-zip` and
  `win-arm64-zip`), the `SHASUMS256.txt` format, and the zip's top-level folder. Which versions ship corepack,
  and whether the oldest version still in active use has an arm64 zip.
- **Python:** the python.org install-manager index (`index-windows.json` and any paging): fields, hash format, how
  pre-releases and free-threaded builds are marked, how far back it goes. The package layout, and whether
  `pip` is present or needs `python -m ensurepip` after unpacking.
- **Python relocation:** run from an arbitrary folder with nothing in the registry: `python -m venv`, `pip install`
  and `import ssl, sqlite3, tkinter` all work.
- **Long paths:** unpacking Node's deep `node_modules\npm\...` under `%LOCALAPPDATA%\tack\installs\` works.
- **EDR:** whether unpacking and running from the installs folder trips CrowdStrike or Defender on this machine.
  If it's blocked, stop and raise it with SecOps; don't look for ways around it.

**Done when:** each source's index, hash and layout is written down with a trimmed sample saved as a test
fixture, and any decision above that turned out wrong is amended here.

### Checkpoint 1: ADR 0003 and the config model ✅ 2026-10-02

Done: [ADR 0003](adr/0003-managed-installs.md). Two things came out of it. `VersionOrder` also fixed prefix
matching, which used to pick `3.15.0rc2` over `3.15.0` for a `3.15` pin. And `tool list` shows extra bin folders
under the version's path (`also:` with `--expand`).

- `docs/adr/0003-managed-installs.md`: tack may download tools, on request only. It records I1, I3, I7 and the
  trust position. Downloads run as you with no elevation, into folders you own: the same baseline as T5 in the
  [trust model](design/dispatch-trust-model.md), and the same as fnm or scoop.
- `InstalledVersion`: add `ExtraBinDirs` and `Install` (`Source`, `Url`, `Sha256`, `InstalledAt`), both null by
  default. `ResolvedVersion` gains `ExtraBinDirs`; `ConfigCompiler` copies it across.
- `BinaryLocator` and the shim search `BinDir`, then each extra folder (I5). `which` and `info` use the same code.
- `TackPaths.User.InstallsDir` (plus its `.staging`, `.trash` and `.cache` folders).
- `VersionOrder` (I14), with `ToolRegistry.Remove` switched over to it.
- Pull registration out of `ToolsAddCommand` into Core (`ToolRegistry.Register`) for I16. `tool add` keeps its
  behaviour and output.

**Done when:** the unit tests pass, `ShimTests` pass with a version that exposes a command from an extra bin
folder, and `tool add` behaves exactly as before.

### Checkpoint 2: sources, version specs, aliases (pure) ✅ 2026-10-02

Done: `src/Tack.Core/Installs/` (`IToolSource`, `NodeSource`, `PythonSource`, `ToolIndex`, `VersionSpec`,
`Checksums`), tested in `InstallSourceTests`. Beyond the plan:

- **Index hardening.** Sources skip any version that isn't a plain version string (an index can't name
  `..\..\x` and have it become a folder) and any archive that isn't on HTTPS. `next` links must stay on the
  index's host and HTTPS, can't loop, and stop at 10 pages.
- **Pages are merged per version,** so an x64 build on one page and an arm64 build on another make one version.
- **Python is parsed by `id`, not `tag`:** pre-releases are tagged `3.15-dev-64`. All 519 `pythoncore` entries in
  the real index match the pattern.
- **`node@v20` and `3.15.0RC2` are accepted** as `20` and `3.15.0rc2`.

- `Installs/IToolSource`: `Parse(index) -> RemoteVersion[]` (version, LTS name, pre-release, per-architecture
  archive URL and hash) and `Plan(version, arch) -> InstallPlan` (URL, expected SHA-256, folder to strip,
  exposes, extra bin folders, post-install step).
- `NodeSource` and `PythonSource` over the checkpoint 0 fixtures. Node's hash comes from `SHASUMS256.txt`, so
  planning a Node install takes that file as a second input. Python's index is paged (`next`), unsorted, and
  mixes `pythoncore-`, `pythonembed-` and `pythontest-` entries plus hashless NuGet ones. The source takes the
  `pythoncore-` entries for this architecture that aren't free-threaded and have a hash.
- Spec resolution: exact version, dotted prefix (`20`, `3.12`, never `3.121`), `latest`, `lts` (Node only), with
  the I13 pre-release rules.

**Done when:** every resolution rule has a fixture test, including the errors (unknown version, `python@lts`, no
build for this architecture, only a pre-release matches).

### Checkpoint 3: the install engine ✅ 2026-10-02

Done: `Installer`, `HttpDownloader`, `ProcessRunner` and `IndexCache` in `src/Tack.Core/Installs/`, tested in
`InstallerTests`. Where it differs from the plan:

- **A lock file, not a mutex,** serialises installs and removals (`installs\.lock`, deleted on close). A mutex
  belongs to a thread, and an install awaits across several.
- **Archives are size-checked before unpacking:** at most 200,000 entries and 4 GB unpacked.
- **Ownership (I7) is stricter than "has a marker":** the folder must be exactly `<tool>\<version>` under
  `installs\` (not a dot-folder), not a junction or symlink, and its marker must name that same tool and version.
- **Redirects are followed by hand,** at most 5, each one checked to be HTTPS.
- **The cache stores each index page** (`.cache\<tool>-<url hash>.json`), so Python's three pages age
  independently. Offline, an older copy is used and its age is reported.
- **Confirmed on this machine:** an open handle on a file inside a version's folder stops the rename to `.trash`,
  so an in-use version fails cleanly.

- `IDownloader` with an `HttpClient` implementation (HTTPS only, progress callbacks, cancellable) and a fake one
  for tests.
- `Installer.Install(plan)`: take the mutex, sweep stale staging, download, check the SHA-256 (I3), unpack
  (I11), write `.tack-install.json`, rename into place, run the post-install step there (I10, I17), then check
  the exposed files exist. A failure before the rename deletes the staging folder; one after it moves the folder
  to `.trash\`.
- `Installer.Remove(path)`: the I7 ownership checks, then rename to trash, then delete (I9).
- Index cache: one hour, `--refresh` to bypass. A failed fetch with a cache available says it's using the cache
  and how old it is.

**Done when:** tests cover a hash mismatch, a zip-slip entry, a missing exposed file, a cancelled download,
leftover staging from a killed process, a folder outside the installs root, a folder without a receipt, and a
locked folder on remove.

### Checkpoint 4: `tack tool install` ✅ 2026-10-02

Done: `ToolsInstallCommand` (`src/Tack.Cli/Commands/InstallCommands.cs`), plus the opt-in `LiveInstallTests`
(`TACK_LIVE_TESTS=1`). Checked by hand against the dev profile:

- `node@lts` resolved to 24.21.0 and was **refused**, because the dev profile already had 24.21.0 registered
  from fnm (I8).
- `node@20` installed 20.20.2, and `python@3.12` installed 3.12.10 with `pip` and `pip3` exposed from `Scripts\`.
  Through the dev shims under a `tack.yml` pin, `node -v`, `npm -v`, `python -V`, `pip -V` and `python -m venv`
  all worked. npm's global prefix is the install folder. A repeat install is a no-op.
- **EDR:** nothing was blocked downloading, unpacking or running from `%LOCALAPPDATA%\tack (Dev)\installs\`
  (carried over from checkpoint 0).
- **I17's isolation flags are proven:** with `PIP_REQUIRE_VIRTUALENV=1` set, the bare pip command fails (exit 3)
  and tack's `-I --isolated` one succeeds.
- **Not checked by hand:** Ctrl-C mid-download. The engine's cancel path is unit-tested; the CLI wires it to
  `Console.CancelKeyPress`. Checkpoint 8 covers it.
- `tool add` and `tool install` now share their closing notes (`ToolsAddCommand.Reach`).

- `ToolsInstallCommand`: resolve the spec, print `node@20 -> 20.11.1`, refuse collisions (I8), download with a
  Spectre progress bar (like `UpdateCommand`), install, register through `ToolRegistry.Register`, `Shims.Sync`,
  then the usual shadowed-name report and the disabled and not-on-PATH notes from `tool add`.
- An error for an unsupported tool lists the ones that are supported and points to `tool add` for everything else.
- Offline or proxy failures name the URL that failed and what to check.

**Done when:** on this machine, `tool install node@lts` and `tool install python@3.12` work end to end, and
`node -v`, `npm -v`, `python -V` and `pip -V` run through the shims under a `tack.yml` pin. The live test
category covers the same.

### Checkpoint 5: managed versions in `remove`, `list`, `info` and `doctor` ✅ 2026-10-02

Done, and checked by hand against the dev profile, which was put back afterwards:

- **The first run failed the "Done when".** With node running from `installs\node\20.20.2`, the rename into
  `.trash` succeeded: Windows renames a folder under a running exe and only refuses to delete the exe. So the
  version was unregistered while in use, and `node.exe` was left in `.trash`. The fix is
  `Platform.RunningProcesses`, which finds processes by image path and is checked before the rename (I9). On the
  rerun, removal failed naming `node.exe (pid 34428)` with the config and folder untouched, then succeeded once
  node exited.
- **`--keep-files` disowns the folder** (deletes its marker). Without that, `doctor --fix` would later have
  deleted the files you asked to keep, as an unregistered folder tack owns.
- **Removal deletes files first, then unregisters only what was deleted.** One version in use doesn't stop the
  others going. A version recorded as managed whose folder tack doesn't own (a hand-edited config) is refused,
  with a hint to use `--keep-files`.
- **`doctor`** reports leftovers in `.staging` and `.trash`, and install folders nothing is registered for. It
  says whether tack made them, and `--fix` deletes only those. A missing managed version says to reinstall it.
  `installs\` is in the folder-permission check. The leftover from the failed first run was found and swept this
  way.
- **`tool list`** shows `managed` (with `source:` under `--expand`), and **`info`** says where the version came from.

- `tool remove`: managed versions delete their files (I9). The picker labels them `managed, deletes files`.
  `--keep-files` keeps them (the folder stays, the receipt is removed from config). Removing a Python warns that
  venvs made from it stop working (`pyvenv.cfg` points at it). The in-use error names the usual culprits: a
  running tool, or a terminal whose current directory is inside the folder.
- `tool list`: a `managed` marker. `--expand` adds the source line. `info <tool>` shows where it came from.
- `doctor`: a missing managed folder says to reinstall with `tack tool install <tool>@<version>`. Folders under
  `installs\` that nothing is registered for are reported, and `--fix` deletes them if they have a receipt.
  Leftover `.trash` and `.staging` are cleaned by `--fix`. The folder-permission check from the user-path plan
  covers `installs\` explicitly.

**Done when:** removing a managed version with node running from it fails without changing the config, and
succeeds once node exits.

### Checkpoint 6: `tack tool available` ✅ 2026-10-02

Done: `Available` in Core (tested in `AvailableTests`) and `ToolsAvailableCommand`. A line is one segment for Node
and two for Python (`IToolSource.LineSegments`). The default view and a prefix list only what `tool install` can
install here. A prefix includes that line's pre-releases. `--all` adds the rest, marked with the reason
(`no checksum published`, `no arm64 build`). Versions registered with `tool add` show as `registered`, managed
ones as `installed`. Offline was checked with a dead proxy: `available --refresh` used the cached list and said
how old it was, and `install` with nothing cached failed cleanly, leaving nothing behind.

- `tack tool available <tool> [prefix]`: by default the newest version of each major (Node) or minor (Python),
  with LTS names, pre-releases hidden, and versions you already have marked `installed`. A prefix lists every
  match; `--all` lists everything.

**Done when:** it works from the cache when offline and says how old the cache is.

### Checkpoint 7: docs ✅ 2026-10-02

Done. The README has the new bullet and a **Managed installs** section, and also mentions `installs\` under
uninstalling, updating, the dev profile and the quick start, plus `TACK_LIVE_TESTS` under development. The scope
plan's non-goal is struck through and points to ADR 0003, and M10 is listed among the milestones. The CHANGELOG
needed nothing: v0.2.1 already lists every user-facing change, and a README edit isn't something
`tack changelog` should report.

- README: the "Bring your own installs" bullet becomes "bring your own installs, or let tack fetch Node and
  Python". A new **Managed installs** section covers the layout, integrity (and its limits), remove semantics and
  the npm and pip behaviour from the open questions below. The antivirus note mentions `installs\`.
- Scope plan §2: mark the non-goal as lifted, pointing to ADR 0003.
- CHANGELOG `[Unreleased]`.

### Checkpoint 8: manual end-to-end check (you run it)

From a local `publish.bat` build, on a profile with nvm and fnm removed:

1. `tool available node`, then `tool install node@lts` and `tool install node@20`. Pin each in a `tack.yml` and
   run `node -v` from a terminal, from Visual Studio and from a scheduled task that runs as you.
2. `tool install python@latest`. Run `python -m venv .venv`, activate it, and check that `pip install` lands in
   the venv.
3. `npm i -g typescript` and `pip install black`: record where they land and whether `tsc` and `black` can be
   run (see the open questions).
4. Remove a version while it's running, then after it exits.
5. Go offline: `available` uses the cache, and `install` fails with a clear message and nothing half-installed.
6. `tack update`: the installs survive.

## Open questions (not blocking checkpoint 0)

- ~~**Global npm packages.**~~ Resolved by checkpoint 0: the zips don't ship the MSI's `npmrc`, so npm's global
  prefix is the install folder itself. `npm i -g` is per version, like nvm on Unix, and lands in `BinDir`.
- ~~**Commands installed later.**~~ Done after v0.2.2: `tack reshim` (no longer hidden) rescans each managed
  version's `BinDir` and `ExtraBinDirs` (`ManagedCommands`). It skips each source's `NotCommands` (`install_tools`,
  `nodevars`, `pythonw`) and any name another tool already provides, and drops names whose files have gone.
  Running it automatically after `npm i -g` is still open (scope plan §5.6).
- **Signatures.** Node's GPG-signed `SHASUMS256.txt.sig` and python.org's Sigstore bundles would turn I3's
  integrity check into an authenticity check. Both need a verification library in the CLI. This matters more
  than it first looked: behind a TLS-inspecting gateway (found after checkpoint 6, see the
  [findings](m10-spike-findings.md#behind-a-tls-inspecting-gateway-found-after-checkpoint-6)), the archive and its
  hash both arrive through something that could rewrite them.
- **More tools.** The obvious next sources are .NET SDKs, Go and Java (Temurin). `IToolSource` should take them
  without changes to the engine, which is the test of whether checkpoint 2's interface is right.
- **A mirror setting** for networks that block nodejs.org or python.org (I15).
