<h1 align="center">tack</h1>
<p align="center">
 <img src="./tack.png" width="150"  />
</p>

<p align="center">
<strong>Folder-based version pinning that even your IDE will respect.</strong>
</p>

<br>

Tack is a **Windows** per-directory tool-version dispatcher. You install the versions of Node, Python or any other CLI yourself; tack makes sure that whenever *anything* runs a bare `node`, it gets the version that directory is supposed to use. That includes Visual Studio, Rider, a pre-build event, a background test runner or a scheduled task, none of which ever sourced your PowerShell profile.

Already know you want it? [Skip the details and install it ▼](#installing)

### At a glance

- **Reaches what shell hooks can't.** `nvm use` and `mise activate` only change a shell that ran a hook. tack's shims are real files on PATH, so a GUI, a service and a terminal all hit them the same way.
- **Bring your own installs.** tack doesn't download anything. Register what you already have (winget, an archive, nvm-windows) and tack handles the dispatch.
- **Pin it in the repo**, with a two-line `tack.yml` that's picked up the moment it's saved. No reshim needed.
- **…or pin it without touching the repo.** A **zone** pins a directory and everything under it from your machine's own config. For the employer repo where nobody wants your tooling files committed.
- **Always explains itself.** `tack info` tells you which version a directory gets *and the exact rule that won*.
- **Invisible where it isn't configured.** No rule? The command falls straight through to whatever was next on PATH, as if tack weren't there.
- **An off switch.** `tack disable` / `tack enable`, instantly, with no PATH edits and no admin rights.
- **A doctor that fixes things.** `tack doctor` names whatever is shadowing the shims (looking at you, nvm-windows); `tack doctor --fix` sorts it out.

## How it works

tack puts one directory of tiny proxy executables — **shims** — at the front of your PATH. There's one per command it manages: `node.exe`, `npm.exe`, `npx.exe`, `python.exe`… When something runs `node`, it's the shim that starts. The shim looks at the caller's working directory, decides which registered version applies there, then runs that version's real binary with the same arguments, streams and Ctrl-C handling, and exits with its exit code.

The shim is a NativeAOT binary built for fast start-up, since it runs on every single tool call. It reads one precompiled lookup file and only goes looking for a `tack.yml` on disk when there is one.

Commands a version ships alongside its main binary always follow it. Pin `node@20` in a directory and `npm` and `npx` there are node 20's too.

### Which version wins

For any directory, tack takes the first rule that applies, top to bottom:

1. **`TACK_<TOOL>_VERSION`** environment variable, e.g. `TACK_NODE_VERSION=18.19.0`. The escape hatch, handy in CI.
2. **An enforced zone.** A zone marked `--enforce` beats the repo's own `tack.yml`.
3. **The nearest `tack.yml`**, walking up from the current directory to the drive root.
4. **The deepest zone** containing the current directory.
5. **The tool's default**, which is the first version you registered.
6. **Passthrough.** Nothing matched, so run whatever is next on PATH.

Versions match by dotted prefix, so `python: "3.12"` picks the highest registered `3.12.x` (and never `3.121`).

## Features

### `tack.yml` — pinning in the repo

```yaml
tools:
  node: 20.11.0
  python: "3.12"
```

Drop it in a project root, or let `tack use node@20.11.0` write it for you. It deliberately understands only this flat `tools:` map, so the shim can read it fast without a YAML library. It's picked up the moment it exists; there's nothing to refresh.

### Zones — pinning without the repo

```powershell
tack zone add C:\work\employer node@18
tack zone add C:\work\employer\new-thing node@22   # deeper zones win
```

A zone is a plain directory, and it covers itself and everything under it. Every zone that applies to a directory is one of its parents, so the deepest one always wins outright. There's no specificity scoring and there are no ties. Adding a zone that already exists for the same directory and tool updates it. `tack zone list` shows them all, and `tack zone remove` with no arguments opens an interactive picker.

- **`--enforce`** lifts a zone above a repo's `tack.yml`, for when the machine's rule has to win.
- **`node@none`** switches tack off for node in that directory tree, so `node` runs whatever is next on PATH. A deeper zone can switch it back on.
- **`none`** on its own (`tack zone add C:\legacy none`) switches tack off for *every* tool there, including ones you register later.

### The registry

```powershell
tack tool add node@20.11.0                       # finds node on PATH for you
tack tool add node@18.19.0 --path C:\node\18.19  # or point at the install
tack tool list
tack tool remove                                 # interactive multi-select
```

`tool add` works out which commands an install provides by scanning its folder (override with `--exposes node,npm,npx`). Leave out `--path` and it finds the tool on PATH the way `where` does, skipping tack's own shims. If there's more than one match, you pick. PATH entries like `%NVM_HOME%` are expanded, so an nvm-windows install is registered by its real directory.

Removing a version tidies up after itself: a tool left with nothing is dropped, a default pointing at the removed version moves to the highest one left, and any zone still pointing at it is flagged for you to fix.

### Seeing what's going on

- **`tack info`**: a table of every registered tool, the version it resolves to in the current directory, and why.
- **`tack info node`**: the same for one tool, as a tree showing the winning rule and the exact binary that will run.
- **`tack which node`**: just the absolute path, for scripts.
- **`tack doctor`**: a PATH health checklist. Is the shims dir on PATH, and ahead of the installs it has to beat? Are any registered folders missing, or any old config needing attention?

### PATH repair

Windows builds the effective PATH as *system entries, then user entries*. A shims dir on the user PATH therefore still loses to any system-wide Node or Python. **`tack doctor --fix`** regenerates the shims and moves the shims dir to the **front of the system PATH**. It asks for elevation through UAC for that one write, and only that write.

It edits the registry value directly, so tokens like `%SystemRoot%\system32` and `%NVM_HOME%` stay as tokens instead of being baked into literal paths. It prints the full before/after and saves a timestamped backup under `%LocalAppData%\tack\path-backups\`, so any edit can be undone by hand. The user PATH is left alone.

### The off switch

`tack disable` parks the shims folder under another name. The PATH entry then points at nothing, and every tool falls straight through to the real PATH, even in shells that are already open. `tack enable` puts it back. There are no PATH edits and no admin prompt, which makes this the quickest way to answer "is this tack's fault?"

Configuration keeps working while tack is disabled: add tools and zones as normal, and they go live the moment you re-enable.

### The little things

- **`tack changelog`** shows what's new in the latest release (`--all` for the full history).
- The shims carry honest Windows file metadata, so a copied `node.exe` identifies itself as tack rather than as an anonymous unsigned binary.
- Reshims leave unchanged shims alone. A shim that's in use, or being scanned by antivirus, is moved aside rather than causing an error.
- No admin rights anywhere, apart from the one opt-in `doctor --fix` write.

## Installing

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/tack/main/install.ps1 | iex
```

That's the whole install. It needs no admin rights: tack installs to `%LocalAppData%\Tack\`, puts `tack` and the shims dir on your user PATH, and adds a normal uninstaller under Settings → Apps. Open a **new** terminal afterwards so it picks up the PATH change, then:

```powershell
tack tool add node@20.11.0
tack info
```

What the script does, in order: resolves the latest release, fetches `SHA256SUMS.txt` and `Tack-win-Setup.exe`, **checks the installer against the manifest and deletes it rather than run it on any mismatch**, then hands off to the installer. It's [`install.ps1`](install.ps1) in this repo — read it before piping it into your shell, the same as you should with any installer.

Pin a version instead of taking the latest:

```powershell
$env:TACK_VERSION = '0.1.0'; irm https://raw.githubusercontent.com/ArcticGizmo/tack/main/install.ps1 | iex
```

Because PowerShell rather than a browser does the downloading, nothing is tagged with the mark-of-the-web — so this route never hits the **"Windows protected your PC"** SmartScreen wall.

### Installer by hand

Prefer to click things: download `Tack-win-Setup.exe` from the
[latest release](https://github.com/ArcticGizmo/tack/releases/latest) and run it. Identical install,
identical self-updates.

A browser download *is* tagged with the mark-of-the-web, so SmartScreen shows the blue **"Windows protected
your PC"** dialog — click **More info → Run anyway**, or use the one-liner above and skip it. To check the
download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Tack-win-Setup.exe').Line.Split()[0]
(Get-FileHash Tack-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

### A note on antivirus

tack copies one small exe under the names of real tools and puts it on PATH. That's exactly what it's for, but it can look suspicious to endpoint security. The binaries aren't code-signed yet. On a managed machine (CrowdStrike, Defender for Endpoint and so on), ask your security team to allow-list `%LocalAppData%\Tack\` and `%LocalAppData%\tack\shims\` before a quarantine does it for you.

### Uninstalling

Uninstall from Settings → Apps. The PATH entries are removed, but your registry, zones and shims under `%LocalAppData%\tack\` are kept, so a reinstall picks up where you left off. Delete that folder too if you want a clean slate. (If you ran `tack doctor --fix`, remove the shims entry from the system PATH yourself; the backup file shows what it was before.)

## Updating

```powershell
tack update --check   # is there anything new?
tack update           # get it
```

`tack update` downloads the latest release and swaps it in as the command exits. The next `tack` you type is the new one. Every shim is then restamped with the new build, so fixes to the proxy reach tools you registered long ago. Run `tack changelog` to see what changed.

The one-liner is only a verified download-and-run wrapper around the installer, so an install through it is
an ordinary installed copy — Velopack owns everything from there, and you never need to re-run the script.
Only a hand-extracted portable zip is outside that: download the new release and replace the folder.

## Building a release (maintainers)

Releases are created by pushing a version tag. GitHub Actions handles the build and publishes the artifacts to the GitHub Release automatically.

**Steps:**

1. Bump `<Version>` in `Directory.Build.props` and add the release's section to `CHANGELOG.md` (the
   `bump-version` Claude skill does both, working from the last git tag)
2. Commit the change
3. Push a matching tag:
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
4. GitHub Actions publishes `tack.exe` and the NativeAOT `tack-shim.exe`, records the shim's start-up latency
   in the run summary, and packs both with Velopack. The `release` job then flattens the artifacts, writes
   `SHA256SUMS.txt` over them, and uploads the lot to the release page

Everyone can then run `tack update` to get the new version.

### Checksums (maintainers)

`SHA256SUMS.txt` is generated in the `release` job from the exact files about to be uploaded — never in a
build job — so it can't drift from what users download. It's plain `sha256sum` format, so
`sha256sum -c SHA256SUMS.txt` works on it directly.

Two guards run before it's written, because [`install.ps1`](install.ps1) depends on the result and a silently
wrong manifest is worse than a failed release:

- a duplicate asset basename fails the release rather than publishing one of two different files under one
  name;
- a missing `Tack-win-Setup.exe` fails the release, since the one-liner resolves that asset by name.

`install.ps1` refuses to install a release that has no `SHA256SUMS.txt`. If you ever hand-upload a release,
run `publish.bat` (it writes the same manifest into `releases\`) and include the file.

**What this does and doesn't buy.** Checksums prove the bytes you ran are the bytes that were published —
they catch truncated downloads, a proxy or mirror rewriting the payload, and tampering anywhere between
GitHub and the disk. They are *not* a signature: the manifest lives on the same release as the installer, so
anyone who could replace one could replace both. Closing that gap needs code signing, which is still to come.

`install.ps1` must stay **pure ASCII**. It has no byte-order mark, so Windows PowerShell 5.1 reads it in the
system codepage, where a stray em dash becomes a curly quote that PowerShell treats as a string delimiter.
[`tools/test-install.ps1`](tools/test-install.ps1) checks that and the installer's other hard-won lessons.

### Building locally (optional)

If you want to produce release artifacts without pushing a tag, install the `vpk` CLI once:

```
dotnet tool install -g vpk
```

Then run:

```
publish.bat        # Tack-win-Setup.exe + SHA256SUMS.txt
```

Artifacts land in `releases/`. NativeAOT needs Visual Studio's **Desktop development with C++** workload to
link. Without it, `publish.bat` falls back to a self-contained single-file shim, which works but starts
noticeably slower than a CI build. `publish.bat` hashes everything currently in `releases\`, which
accumulates older versions' `.nupkg`s locally; CI starts from a clean artifact set.

## Development

Requirements: .NET 10 SDK

```
run.bat --help          # the CLI, from source
dotnet test             # the test suite
```

`Tack.Core` is the engine (config, resolution, reshim and PATH handling). `Tack.Cli` is the Spectre.Console
front end, and `Tack.Shim` is the NativeAOT proxy. The CLI only formats output: every decision is made in
Core, so it's all unit-testable.

Debug builds run as an isolated **dev profile**. They keep their own config, shims and `resolved.json` under
`%LocalAppData%\tack (Dev)\`, so hacking on tack never touches your real setup. Set `TACK_DEV=0` to point a
debug build at the real profile, or `TACK_DEV=1` to force a release build into dev.

To try a dev build as if it were really installed, run `run.bat doctor --fix`. This puts the dev shims at the
front of the system PATH, **directly behind** the release tack's. The dev build then beats every real install
but never the release tack. Run the release `tack disable` to hand shared commands like `node` over to dev,
and `tack enable` to take them back.

Useful switches for debugging the shim:

- `TACK_SHIM_DEBUG=1` makes the shim trace its decisions to stderr.
- `TACK_RESOLVED=<path>` points the shim at a specific `resolved.json`.

[`tools/bench-shim.ps1`](tools/bench-shim.ps1) measures the shim's start-up overhead against a stub tool.
