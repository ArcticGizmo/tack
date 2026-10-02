# M10 checkpoint 0: spike findings

*Run 2026-10-02 on `feature/managed-installs`, x64 Windows 11, with CrowdStrike and Defender running. The
spike is `spikes/m10-installs/`: a throwaway .NET 10 console that unpacks with `System.IO.Compression.ZipFile`,
and `make_fixtures.py`, which trimmed the indexes into `tests/Tack.Tests/Fixtures/Installs/`.*

**Verdict: both sources work.** Every archive's SHA-256 matched its vendor value, both runtimes ran from
arbitrary folders with nothing in the registry, and nothing was blocked. Five findings change the plan, and the
amendments are made in [the plan](m10-managed-installs-plan.md):

1. python.org publishes hashed zips only from **3.11.0**, so that's the floor (I12).
2. Python zips have **no `pip.exe`**. tack has to generate it, offline, after the version is in its final folder
   (I10, I17).
3. Those launchers **embed their absolute path**, so a Python install can never be moved after that step (I10).
4. npm's global prefix for a zip install is the **install folder itself**, so npm globals are per version
   already. That resolves the npm open question.
5. Unpacking the whole archive and then moving its top folder into place is simpler than stripping a prefix entry
   by entry, and keeps .NET's built-in zip-slip check (I11).

## Node

| | |
|---|---|
| Index | `https://nodejs.org/dist/index.json`, one JSON array, newest first, 868 entries, 330 KB |
| Per entry | `version` (`"v24.21.0"`), `date`, `files` (`win-x64-zip`, `win-arm64-zip`, ...), `npm`, `lts`, `security` |
| `lts` | `false` or the line's codename (`"Krypton"`, `"Jod"`, `"Iron"`) |
| Archive | `https://nodejs.org/dist/v{v}/node-v{v}-win-{x64,arm64}.zip` |
| Hash | `https://nodejs.org/dist/v{v}/SHASUMS256.txt`: `<sha256>  <filename>` per line (two spaces) |
| Coverage | `win-x64-zip` since v4.5.0; `win-arm64-zip` since **v19.9.0**, so Node 18 and older on arm64 fails (I6) |
| Layout | one top-level folder, `node-v{v}-win-x64\`; `node.exe`, `npm.cmd`, `npx.cmd` at its root |

- **Folder contents:** the root also holds `install_tools.bat` and `nodevars.bat`, which confirms I4 (the source
  names the commands; it doesn't scan the folder). `corepack.cmd` is there in 20.20.2 and 24.21.0 and gone in
  26.10.0. 25.x wasn't checked; filtering the declared names by what exists makes it moot.
- **npm globals:** the zips don't ship the `node_modules\npm\npmrc` that the MSI adds (`prefix=${APPDATA}\npm`).
  So `npm config get prefix` is **the install folder itself** for all three versions. `npm i -g typescript` puts
  `tsc.cmd` next to `node.exe`: per version, and already in `BinDir`. It just isn't shimmed, because exposes are
  fixed at install time (open question 2).
- 20.20.2, 24.21.0 and 26.10.0 ran `node -v` and `npm -v` straight from the unpacked folder.

## Python

| | |
|---|---|
| Index | `https://www.python.org/ftp/python/index-windows.json`, `{ "versions": [...], "next": "..." }` |
| Paging | `index-windows.json` → `index-windows-recent.json` → `index-windows-legacy.json` (no `next`). 3 pages, about 900 KB, 963 entries, no version repeated across pages |
| Order | **not sorted**: page 1 starts 3.13.16, 3.14.8, 3.15.0rc2. tack sorts with `VersionOrder` (I14) |
| `id` | `pythoncore-3.13-64`, `-32`, `-arm64`; free-threaded is `pythoncore-3.13t-64`. Also `pythonembed-*` (the embeddable zip) and `pythontest-*` (the test suite), which tack ignores. Parse the `id`, not the `tag`: a pre-release's tag is `3.15-dev-64` (found in checkpoint 2) |
| `sort-version` | `3.14.8`, `3.15.0rc2`, `3.15.0b4`, `3.15.0a7`. Pre-releases are marked only by the letters |
| Archive + hash | `url` plus `hash.sha256` |
| Coverage | hashed `www.python.org` zips for **3.11.0 onwards**. 3.5.2 to 3.10.11 (and 2.7.18) point at `api.nuget.org` `.nupkg` files with **no hash**. arm64 starts at 3.9.7 (NuGet), so 3.11 for hashed zips |
| Layout | **flat**: `python.exe`, `pythonw.exe`, `python3XX.dll`, `Lib\`, `DLLs\` at the root; no top folder |
| Other fields | `alias` (the `python3.exe` and `python3.13.exe` names the install manager creates), `install-for`, `executable`; NuGet entries also carry `run-for` and `shortcuts` (PEP 514 keys, Start menu). tack uses none of them in the first cut |

- **Security-only releases have no Windows build.** `python-3.12.12-amd64.zip` is a 404; the newest 3.12 with a
  Windows build is **3.12.10**. tack must resolve from the index and never build a URL itself.
  `python@3.12` gets 3.12.10.
- **3.10 and older** are only on NuGet without a hash. Failing closed (I3) puts the floor at 3.11. 3.10 reaches
  end of life this month anyway.

### It runs from anywhere

From the unpacked folder, with nothing in the registry, both 3.12.10 and 3.14.8:

- `python -V`, `python -m pip -V` (pip 25.0.1 and 26.2.1 are installed in `Lib\site-packages`),
- `import ssl, sqlite3, tkinter, ctypes, venv` (OpenSSL 3.0.16 and 3.5.9),
- `python -m venv` to a sibling folder, then `pip install six` inside the venv. `pyvenv.cfg` records the base
  install's absolute path, as with any Python, so **moving or deleting a managed version breaks venvs made from
  it.** Remove has to say so (checkpoint 5).

The user site is enabled, so `pip install --user` goes to `%APPDATA%\Python\Python3XX\`, which is shared by every
install of that minor version. That's standard Python behaviour, and tack leaves it alone.

### There's no `pip.exe`

The zip has no `Scripts\` folder, so a shim for `pip` would have nothing to run.

- `python -m ensurepip --upgrade --default-pip` **doesn't help**: pip is already installed, so it does nothing.
- `python -m pip install --force-reinstall --no-index --no-deps --find-links Lib\ensurepip\_bundled pip`
  **works offline**, from the wheel in the zip. It creates `Scripts\pip.exe`, `pip3.exe` and `pip3.XX.exe` on 3.12
  and 3.14 alike.
- **The launchers embed the absolute path** of the folder they were made in. After renaming the folder,
  `Scripts\pip.exe -V` exits with **1 and prints nothing**. So this step has to run in the version's final
  folder, after the rename out of staging. Any launcher pip installs later (`black.exe`) has the same property,
  so a managed Python can never be moved. That rules out a future "move the installs root" command, or makes it
  a reinstall.

## Unpacking

- **Zip slip:** .NET 10's `ZipFile.ExtractToDirectory` refuses an entry named `../escaped.txt` with
  `IOException: Extracting Zip entry would have resulted in a file outside the specified destination directory`.
  A per-entry extractor would have to do that check itself. Extracting the whole archive and then moving the
  archive's top folder into place keeps the built-in check (I11).
- **Path length:** the longest Node entry is 128 characters
  (`node_modules\npm\node_modules\validate-npm-package-license\node_modules\spdx-expression-parse\package.json`).
  Under `C:\Users\<name>\AppData\Local\tack\installs\node\20.20.2\` that's under 200 characters, well inside
  MAX_PATH even for a long user name or the `tack (Dev)` profile. Python's longest entry is 88 characters.
- **Speed:** unpacking took 3 to 4 s for Node (about 2,400 files) and 5 to 7 s for Python (2,200 to 3,800 files),
  presumably mostly on-access scanning. The install command needs a visible "unpacking" stage, not just a
  download bar.
- **A folder can't be renamed while anything holds it open.** That came up by accident: the spike's own shell
  had its working directory inside an unpacked Python, and the rename failed with "resource busy" although no
  python process was running. This supports I9, and the remove error should name the usual culprit: a terminal
  whose current directory is inside the version's folder, not just a running tool.

## EDR

Nothing was blocked or quarantined. That covered `curl` downloading five archives, the .NET console unpacking
them, running `node`, `npm.cmd`, `python`, the regenerated `pip.exe` and a venv's `python`, and pip downloading
from PyPI. All of it ran under the repo folder. Running from `%LOCALAPPDATA%\tack\installs\` itself is untested
until checkpoint 4's first real install.

## Fixtures for checkpoint 2

`tests/Tack.Tests/Fixtures/Installs/` (the full indexes stay in the git-ignored `spikes/m10-installs/data/`):

| File | What it covers |
|---|---|
| `node-index.json` | the newest of each major from 16 to 26 plus 24.20.0, 24.0.0, 20.11.1 and 20.9.0: LTS and non-LTS lines, prefix ties, 18.x with no arm64 zip |
| `node-24.21.0-SHASUMS256.txt` | the real file, unmodified |
| `python-index-windows.json` | page 1, with `next`: every variant (core, `t`, embed, test; 32, 64, arm64) of 3.13.16, 3.14.8 and the pre-release 3.15.0rc2 |
| `python-index-page2.json` | page 2, no `next`: 3.12.10, 3.12.9, 3.11.0 and the hashless NuGet 3.10.11 |
