# Adversarial review: security and code quality

*Reviewed 2026-09-29 against `main` @ `efa659e` (v0.1.7).*

This review covers the whole repository, not just a recent diff. It looks at two things:

1. **Security and abuse:** ways another program on the machine could use tack to change data, or run code, that it couldn't already reach.
2. **Code quality:** bugs, duplication and cheap improvements.

Findings marked **confirmed** were reproduced against a real build of the shim or on a real machine. The rest come from reading the code, with the reasoning given.

## Threat model

The attacker is a malicious program running as the tack user, at normal (non-admin) level. Such a program can already read and write everything under `%LOCALAPPDATA%`, the user's registry settings (`HKCU`), and the user's own PATH. So a finding only counts if tack gives it something beyond that:

- running code as **SYSTEM** or as a service,
- running code as **another user** on the same machine,
- running code in the user's own **elevated (admin)** processes (a UAC bypass; Microsoft doesn't formally treat UAC as a security boundary, but malware uses it constantly),
- turning **untrusted data** (arguments, repo files) into commands,
- or getting the user to approve a UAC prompt for something they didn't intend.

## Summary

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | Critical | A user-writable folder is first on the system PATH | confirmed on a real machine |
| 2 | High | `.cmd` targets allow command injection and break on quoted paths | **fixed** (confirmed with the shim) |
| 3 | Medium | Elevated tack writes and deletes inside user-writable folders (junction attacks) | from code |
| 4 | Medium | `apply-machine-path` accepts any folder without checking it | from code |
| 5 | Medium | Auto-detected commands can take over unrelated system commands | from code |
| 6 | Low | Environment-variable overrides are honoured in release builds | from code |
| 7 | Low | Enforced zones are easy to bypass | from code |
| 8 | Low | A repo's `tack.yml` can pick versions that carry environment variables | from code |
| 9 | Low | The invocation log records secrets and can be forged | from code |
| 10 | Low | Release-pipeline hardening | from code |

Fix #2 first because it's small and self-contained. #1 is the most serious but needs a design change.

---

## Security findings

### 1. Critical: a user-writable folder is first on the system PATH

**Status: confirmed on a real machine.**

On a machine where tack is installed, the system (`HKLM`) PATH reads:

```text
entry 1:  C:\Users\<user>\AppData\Local\tack\shims
entry 2:  C:\Users\<user>\AppData\Local\tack (Dev)\shims
entry 31: C:\Users\<user>\AppData\Local\Tack\current
```

All three folders are owned by the user, who has full control over them:

```text
NT AUTHORITY\SYSTEM     FullControl
BUILTIN\Administrators  FullControl
<user>                  FullControl
Owner:                  <user>
```

The system PATH is read by everything on the machine: SYSTEM, services, scheduled tasks, other users' sessions and the user's own elevated processes. That gives an attacker running as the user three separate routes.

#### a) Plant a file in the folder

The attacker drops an exe or DLL into `tack\shims`.

- **Executables:** cmd and PowerShell look up a bare command name by walking PATH in order, after the current folder. Because tack's folder is first, it comes before `%SystemRoot%\system32`. So a planted `net.exe`, `reg.exe`, `sc.exe`, `where.exe` or `powershell.exe` is what any batch or PowerShell script gets when run by SYSTEM, a service, another user or an elevated shell. (`CreateProcess` itself checks System32 before PATH, but scripts don't.)
- **DLLs:** when a program asks for a DLL that isn't installed, Windows' standard search falls back to the PATH folders. Services that load missing DLLs are a well-known route to SYSTEM.

This is the "writable folder on PATH" weakness (CWE-427). Several installers have had CVEs for exactly this, for example Python installed for all users into a writable folder.

#### b) No planting needed: the shim trusts the caller's config file

When SYSTEM or another user runs `node`, tack's shim is what runs. `FindResolved` (`src/Tack.Shim/Program.cs:193-211`) looks for `resolved.json` in this order:

1. `TACK_RESOLVED`
2. next to the shim
3. **the folder above the shims folder, i.e. `C:\Users\<user>\AppData\Local\tack\resolved.json`**
4. the caller's own `%LOCALAPPDATA%\tack`

Step 3 wins for every account. So the tack user's `config.json` decides which binary runs, and with what environment variables, whenever anyone on the machine types `node`. Editing your own config file becomes running code as someone else.

#### c) The user's own elevated shells

The same applies to the user's elevated terminals: a normal-level program edits `resolved.json` and the next `node` in an admin shell runs its code. That's a UAC bypass.

#### d) Logging makes it worse

With `tack log on`, a shim running as SYSTEM calls `Directory.CreateDirectory`, appends to the log, and calls `File.Move(path, path + ".1", overwrite: true)` inside the user's writable `logs` folder (`src/Tack.Core/Diagnostics/ShimLog.cs:86,118`). Creating folder junctions needs no admin rights, so an attacker can point that folder somewhere else and turn those calls into privileged file writes and renames.

#### Fix

Writing only the system PATH is fine. The problem is putting a folder the user can write to on it. These options all keep the rule that tack never writes the user PATH:

- **Move the shims to an admin-owned folder (recommended).** Have `tack setup`, which already runs elevated, create something like `%ProgramData%\tack\shims`, owned by Administrators, with users limited to read and execute. `resolved.json` stays per-user, so elevation is only needed when the *set of command names* changes (a new tool). Version, zone and config changes don't need it.
  - `tack` itself can become one of the shims, forwarding to the caller's own `%LOCALAPPDATA%\Tack\current\tack.exe`. That takes `Tack\current` off the system PATH as well.
- **Make the shim load only the caller's config (do this as well).** Load only `TackPaths.ResolvedJson`, which is computed from the calling account, and never the file next to or above the shim. Pass straight through without reading any config when running as SYSTEM or LocalService, or when the config file's owner isn't the calling user. This closes (b) and (d) but not (a).
- **Store the entry as `%USERPROFILE%\AppData\Local\tack\shims` (not verified).** Because the PATH value is stored unexpanded, each account might expand it to its own folder. It's not certain that this expands at boot for services. If it doesn't, the entry becomes a relative path, which is its own hazard. Test it before relying on it. It also doesn't help (c).

#### Decision (2026-09-29): option B

The two options considered were:

- **A. Fully admin:** config in an admin-only location, and every `tool add`, `zone add` and `log on` asks for UAC. Rejected: every account would share one user's registrations, including binaries in that user's profile, so SYSTEM would still end up running user-writable binaries.
- **B. Admin-installed binaries and shims, per-user config:** chosen.

Under B:

- tack is installed per-machine (Velopack MSI, `--instLocation PerMachine`) into Program Files, and the shims folder is admin-owned. The installer runs elevated, so it writes the system PATH itself. That removes the first-run UAC prompt, `TACK_SKIP_FIRSTRUN`, the setup mutex and `install.ps1`'s separate `tack setup` step.
- Config stays per-user. The shim reads **only** the token user's `TackPaths.ResolvedJson` (`Environment.GetFolderPath` resolves the folder from the process token, not from the `%LOCALAPPDATA%` variable). The `TACK_RESOLVED`, next-to-the-shim and folder-above-the-shims-folder lookups are removed.
- **Who the shim runs as decides whose config it reads:**

  | Caller | Runs as | Config read |
  |---|---|---|
  | Admin user accepting the UAC prompt | the same user | theirs, so pinned versions keep working |
  | Standard user with admin credentials typed at the prompt | the admin account | the admin's own, usually none, so tack passes through |
  | SYSTEM, services, other users | themselves | their own, usually none, so tack passes through |

- **Elevated shells behave like normal ones.** Running as the same account, they read that account's config. Passing through when elevated was rejected because admin shells behaving differently would be surprising. It also isn't a new hole: same-account elevation already trusts files the user can write, such as the PowerShell `$PROFILE`, Python's per-user site-packages, `~/.npmrc` and user environment variables. Microsoft doesn't treat same-account elevation as a security boundary.
- UAC is needed only for install, uninstall, update, and when the set of shim names changes. New versions, zones and settings don't need it.
- Existing machines need migrating: the installer must remove the old `%LOCALAPPDATA%\tack\shims`, `tack (Dev)\shims` and `Tack\current` entries from the system PATH.

Open questions for the first spike:

- whether updates ask for UAC on a Program Files install,
- whether install and uninstall hooks run elevated under the MSI,
- whether the pinned Velopack version supports `--msi`,
- where the dev profile's shims should live.

**Separate gap:** nothing can take the **dev** entry off the system PATH. Only the uninstall hook calls `Unregister` (`src/Tack.Cli/InstallHook.cs:94`), and it only removes the release profile's folder. After `run.bat doctor --fix`, `tack (Dev)\shims` stays on the system PATH for good. Add a way to remove it, for example `tack doctor --unwire`.

---

### 2. High: `.cmd` targets allow command injection and break on quoted paths

**Status: confirmed with a real build of the shim. Fixed:** `.cmd`/`.bat` targets now run as System32's `cmd.exe /e:ON /v:OFF /d /c ""<script>" <args>"`, with each argument escaped by `Tack.Core.Platform.BatchCommandLine`, a port of Rust std's BatBadBut fix:

- arguments containing cmd operators are quoted, and `"` is doubled,
- `%` becomes `%%cd:~,%`,
- an argument with a line break or NUL is refused (exit 127),
- `%ComSpec%` is no longer used.

`BatchCommandLineTests` pins the escaping. Two `ShimTests` check that an npm-shaped `.cmd` in a folder with a space receives `two words`, `a&b`, `c>d`, `%OS%`, `q"uote`, `trail\` and `""` literally, and that a line break is refused.

The original analysis follows.

`Exec` (`src/Tack.Shim/Program.cs:162-189`) runs `.cmd` and `.bat` targets as `cmd.exe /c <target> <args>`, using .NET's `ProcessStartInfo.ArgumentList`. That applies normal exe-style quoting, but cmd.exe parses the command line differently: it treats `&`, `|`, `<`, `>`, `^` and `%` as special, and strips quotes after `/c` in its own way.

A test build of the shim (`tests/Tack.Tests/bin/Release`), renamed `node.exe` and pointed at a test `node.cmd` that prints its arguments, gave:

| Case | Input | Result |
|---|---|---|
| A | target folder without a space: `"a b" plain` | correct: `ARG1=[a b] ARG2=[plain]` |
| B | target folder **with** a space: `"a b" plain` | **fails:** `'...\cmdprobe\dir' is not recognized as an internal or external command` |
| C | `x&echo.INJECTED` | **injection:** the `.cmd` ran, then cmd ran a second command, `echo.INJECTED` |
| D | `%OS%` | **expanded:** the `.cmd` received `Windows_NT` |

**Why B fails:** without `/s`, cmd strips the first and last quote on the line whenever the line has more than two quotes. As soon as the target path and one argument are both quoted, the command line breaks. So `npm install "a b"` against the default `C:\Program Files\nodejs\npm.cmd` doesn't work.

**Why C and D matter:** this is the BatBadBut class of bug (CVE-2024-24576 in Rust, CVE-2024-27980 in Node). After those CVEs, language runtimes either escape arguments specially for cmd or refuse to start a `.cmd` or `.bat` without a shell, but only when they can *see* that the target is a `.cmd` file. With tack in the way, the program being started is `npm.exe`. The caller applies only exe quoting, and the shim then passes those arguments to cmd without escaping them. Code the caller believes is safe becomes injectable:

- `child_process.spawn('npm', [untrusted])`
- `subprocess.run(['npx', package_name])`
- a build tool passing a file name like `a&b`

Node has refused `spawn('npm.cmd', …)` without `shell: true` since CVE-2024-27980. tack makes the same call work again, and makes it injectable again.

#### Fix

- Build the command line by hand rather than with `ArgumentList`: `cmd.exe /d /s /c ""<target>" <args>"`.
  - `/s` together with the outer quote pair makes cmd strip only the outermost quotes, which fixes case B.
  - Quote each argument exe-style, then escape cmd's special characters with `^`. Rust's standard library fix and the `cross-spawn` npm package are good references.
  - `%` can't be reliably escaped inside `cmd /c`. Either use Rust's `%%cd:~,%` trick or refuse the argument.
  - Refuse, loudly, any argument that contains `\r`, `\n` or `\0`.
- `/d` skips cmd's AutoRun registry commands, which also run on every `/c` call and cost time.
- Add cases B, C and D to `tests/Tack.Tests/ShimTests.cs`. None of the current `.cmd` tests use spaces or special characters.

---

### 3. Medium: elevated tack writes and deletes inside user-writable folders

Creating an NTFS folder junction needs no admin rights. So any elevated tack code that works under `%LOCALAPPDATA%\tack` can be redirected by a normal-level process that swaps a folder for a junction.

**Reshim.** Reshim runs elevated in three cases:

- the Velopack install or update hook is already elevated (`InstallHook.Apply`, which explicitly handles this),
- the user runs `tack doctor --fix`, `tool add`, `zone add` and so on from an admin terminal,
- first-run setup, when it's already elevated.

Its clean-up step (`src/Tack.Core/Maintenance/Reshimmer.cs:86-93`) deletes, or renames aside, every `*.exe` in the shims folder that isn't a current shim. Replace `shims` with a junction to `C:\Windows\System32` and that becomes "delete or rename every exe in System32" as admin. Being able to delete arbitrary files as admin is a documented route to SYSTEM (ZDI's Windows Installer rollback technique). The stamping step likewise writes files through the junction.

**The PATH backup.** `apply-machine-path` runs elevated and calls `PathFixBackup.Save(settings.BackupPath, …)` (`src/Tack.Cli/Commands/DoctorCommand.cs:165-166`), which runs `Directory.CreateDirectory` and `File.WriteAllText` under `%LOCALAPPDATA%\tack\path-backups`. A junction there, optionally combined with an object-manager symlink to control the file name, turns that into an admin-level write of any file path. The content is JSON the attacker doesn't choose, but overwriting a system file is still damaging.

**Unchecked command names.** `exposes` entries are never validated. They come from `--exposes` or from a hand-edited `config.json`, and `Path.Combine(shimsDir, name + ".exe")` (`Reshimmer.cs:77`) accepts `..\..\somewhere\x`, which writes outside the shims folder. When reshim runs elevated, that's an admin-level write outside the folder.

#### Fix

- Before any delete, rename or write while elevated, refuse if the target folder or any parent is a reparse point (`FileAttributes.ReparsePoint`). Or refuse to reshim at all while elevated unless the folders are admin-owned (see #1).
- Only delete files that are known shims: an allowlist of `{names}.exe`, the support files and `*.tack-old`, preferably also checked by hash, rather than "any `*.exe` not in the list".
- Have the elevated step write its result to an admin-owned folder, such as `%ProgramData%\tack\path-backups` with users limited to read. The unelevated parent then reads it from there and copies it into the user's own folder.
- Limit command names to `^[A-Za-z0-9._+-]+$`, reject `..`, and check at both `tool add` and config load.

---

### 4. Medium: `apply-machine-path` accepts any folder without checking it

`ApplyMachinePathCommand` (`src/Tack.Cli/Commands/DoctorCommand.cs:146-175`) takes any `--shims`, `--install-dir`, `--behind` and `--remove` values, and writes the system PATH with them.

- **It can be misused.** Anything that can start a process as the user can run `runas tack.exe apply-machine-path x --shims C:\Users\Public\evil`. The UAC prompt names `tack.exe`, a program the user routinely approves. The same works for `--remove --shims C:\Windows\System32 --install-dir C:\Windows`, which strips system folders from PATH.
- **Relative paths resolve from the elevated process's working folder,** which is usually `C:\Windows\System32`, so `--shims .` puts System32 first.
- **The elevated program is user-writable.** `Elevation.RelaunchElevated` starts `Environment.ProcessPath`, which is `%LOCALAPPDATA%\Tack\current\tack.exe`. Swapping that file, or planting a DLL next to it, takes over the next UAC prompt the user approves. Because the binary is unsigned, the prompt shows "Unknown publisher" either way, so the user can't tell anything changed. Code signing was already considered (Azure Trusted Signing was rejected), and this is only noted because the UAC prompt is where signing makes the most visible difference.

#### Fix

In the elevated step:

- accept only absolute paths to folders that exist,
- require them to match tack's own layout: `...\tack\shims`, `...\tack (Dev)\shims`, or a folder that contains `tack.exe`,
- only ever remove entries that match that layout,
- ignore the parent's backup path and write the backup to a location the elevated step chooses itself (see #3).

---

### 5. Medium: auto-detected commands can take over unrelated system commands

`ToolProbe.DetectExposes` (`src/Tack.Core/Maintenance/ToolProbe.cs:11-21`) creates a shim for **every** `.exe`, `.cmd` and `.bat` in the tool's folder. Tool folders often contain extras such as `git.exe`, `ssh.exe`, `curl.exe`, `tar.exe`, `7z.exe` or `unins000.exe`.

Once shimmed, those names sit at the front of the **system** PATH, ahead of System32's own `curl.exe`, `tar.exe` and `OpenSSH\ssh.exe` for cmd and PowerShell lookups. That affects every user and every service (see #1).

Separately, `ConfigCompiler.Compile` (`src/Tack.Core/Config/ConfigCompiler.cs:31`) does `rc.Index[exposed] = toolName`, so the last tool registered wins. If two tools expose the same name, ownership moves silently.

#### Fix

- When detecting commands automatically, show the list and ask before adding. Skip, or at least warn about, names that already exist under `%SystemRoot%` or elsewhere on PATH.
- Report collisions where two tools expose the same name, in `ConfigCompiler` or in `tack doctor`.

---

### 6. Low: environment-variable overrides are honoured in release builds

| Variable | Effect | Where |
|---|---|---|
| `TACK_RESOLVED` | the shim loads any config file, so any binary and environment | `Program.cs:195` |
| `TACK_REPO` | `tack update` installs from any GitHub repo | `UpdateCommand.cs:30` |
| `TACK_DEV` | switches the CLI's profile | `TackProfile.cs:28` |

None of these gives something already running as the user new power, since it could set PATH anyway. But `TACK_REPO` stored in the user's environment settings is a quiet way to stay installed: it survives reinstalls, and "tack update" looks entirely normal.

**Fix:**

- Remove `TACK_RESOLVED` and `TACK_SHIM_DEBUG` from release builds, or only honour a `TACK_RESOLVED` file owned by the calling user.
- Make the repo override an explicit `--repo` flag rather than an environment variable, and print the update source on every update.

### 7. Low: enforced zones are easy to bypass

The code comments call enforced zones "org enforcement" (`Resolver.cs:56`), but:

- `TACK_<TOOL>_VERSION` outranks them (`Resolver.cs:82`),
- zone matching compares path strings, so reaching the same folder through a `subst` drive, a junction, an 8.3 short name or `\\localhost\c$\...` skips the zone.

That's fine for keeping yourself on track, but it isn't a control. Either say so in the documentation, or put the enforced-zone check before the environment variable and compare real paths (`GetFinalPathNameByHandle`), paying that cost only when enforced zones exist.

### 8. Low: a repo's `tack.yml` can pick versions that carry environment variables

The README already notes that a `tack.yml` can select `claude: work` by name. A cloned, untrusted repo can therefore switch its commands to the work account's `CLAUDE_CONFIG_DIR`, and it also beats non-enforced zones.

The search walks up to the drive root (`Resolver.cs:96-104`). On shared drives (other volumes, network shares, folders another user created), another user can plant an ancestor `tack.yml`. The choice is limited to registered versions, which is why this is low.

**Option:** add a per-version flag that makes versions carrying environment variables selectable only by zones and defaults, unless the user opts in.

### 9. Low: the invocation log records secrets and can be forged

- **Secrets:** `ShimLog.Format` writes the full argument list (`ShimLog.cs:61`). Tokens passed as arguments (`--token=…`, `-pPassword`, a `-p <prompt>`) are stored in plain text, and the README invites pasting logs into issues. Environment variable *values* are already left out; arguments deserve similar care. Add a redaction pattern, or record only the argument count by default.
- **Forgery:** arguments and the working folder are written without escaping control characters. An argument containing `\n  runs    C:\legit.exe` creates fake `runs` or `caller` lines. That matters because the log exists to track down a rogue caller. Escape `\r`, `\n` and other control characters.

### 10. Low: release-pipeline hardening

In `.github/workflows/release.yml`:

- `dotnet tool install -g vpk` isn't pinned, while the app pins `Velopack` 1.2.0. A new or compromised `vpk` release goes straight into the installer, and the two versions can drift apart. Pin it with `--version 1.2.0`.
- Third-party actions are referenced by tag. Pin them to commit SHAs, especially `softprops/action-gh-release@v2`, which runs with `contents: write`.
- `permissions: contents: write` is set for the whole workflow, so the build job gets it too. Give it to the `release` job only.
- `${{ steps.version.outputs.VERSION }}` is pasted into `run:` scripts. Pass it through `env:` instead. The risk is low, since only people who can push tags can reach it.
- The README says checksums "are not a signature". `actions/attest-build-provenance` closes that gap at no cost: users can check a download with `gh attestation verify Tack-win-Setup.exe -R ArcticGizmo/tack`, and `install.ps1` could run that check when `gh` is installed. It doesn't depend on the code-signing decision.

---

## Code quality

### Bugs

- **Version sorting is alphabetical.** `UseCommand.PickVersion` (`src/Tack.Cli/Commands/MutateCommands.cs:70`) and the default-version fix-up in `ToolRegistry.Remove` (`src/Tack.Core/Config/ToolRegistry.cs:72`) sort version strings as text, so `9.0.0` beats `20.11.0`. `VersionMatch` already has a correct comparison; make `VersionMatch.Compare` public and use it in both places.
- **`tack use` rewrites `tack.yml` from scratch** (`MutateCommands.cs:55-60,74-81`). It re-parses the file with `MiniTackYml`, then writes back only the `tools:` map, which drops comments and any other keys. Versions are written unquoted, so any other YAML reader sees `3.10` as the number `3.1`. It also doesn't warn when the version isn't registered, although `zone add` does.
- **Saving config isn't atomic.** `ConfigStore.Save` (`src/Tack.Core/Config/ConfigStore.cs:40`) is a plain `File.WriteAllText`. A crash, or two `tack` commands at once, can leave a truncated `config.json`, after which `Load` throws and every command fails. Move `Reshimmer.WriteAtomically` into a shared helper and use it here. Consider a named mutex around load, change and save to avoid lost updates.
- **The "unchanged shim" check can be faked.** `Reshimmer.Stamp` (`Reshimmer.cs:117`) treats a file as unchanged when its size and last-write time match, and both are easy to fake. The shim is small, so comparing a hash is cheap.
- **Clean-up only removes `.exe` files** (`Reshimmer.cs:86`). Anything else in the folder, such as planted DLLs, stays forever. Remove anything not on the known list (see #3).
- **Tool names with non-alphanumerics make awkward variable names.** `TACK_{tool.ToUpperInvariant()}_VERSION` (`Resolver.cs:82`) gives `TACK_CLAUDE-CODE_VERSION`, which is awkward to set from cmd or PowerShell. Replace anything that isn't a letter or digit with `_`, and document the rule.
- **The real tool can outlive the shim.** If the shim is killed (for example an IDE's "stop build", which terminates the process), the child keeps running. Put the child in a Windows job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so it dies with the shim. This is the standard fix and cheap.

### Duplication and refinement

- **Path comparison is written five times:**
  - `PathEdits.Key` (`src/Tack.Core/Platform/PathEdits.cs:87`)
  - `PassthroughScan.Norm` (`src/Tack.Core/Resolution/PassthroughScan.cs:28`)
  - `PathScan.Norm` (`src/Tack.Core/Resolution/PathScan.cs:57`)
  - `PathDoctor.Norm` (`src/Tack.Core/Maintenance/PathDoctor.cs:157`)
  - `Render.Norm` (`src/Tack.Cli/Commands/Render.cs:53`)

  PATH splitting is copied three times, and the executable-extension list twice (`BinaryLocator`, `ToolProbe`). The copies already differ (only `PathEdits` expands `%VARS%`), which is how PATH comparison bugs creep in. Use one shared helper in Core.
- **Two file operations per folder on every call.** The `ReadFileOrNull` lambda is duplicated (`Program.cs:51`, `TackEnvironment.cs:42`), and `File.Exists` followed by `ReadAllText` costs two file-system calls per ancestor folder on every tool call. Just try to open the file and catch not-found. Also cap the size, so a very large `tack.yml` in a parent folder can't stall every tool call.
- **Profile folder names are hard-coded.** `TackPaths.ReleaseShimsDirs` and `AllShimsDirs` repeat `"tack"` and `"tack (Dev)"` instead of deriving them from `TackProfile`.
- **`ApplyMachinePathCommand` is in the wrong file.** It lives in `DoctorCommand.cs`, but `setup` and uninstall use it too. Move it next to `SystemPath`.

### Missing tests

- `.cmd` target in a folder with spaces, called with quoted arguments (#2, case B)
- arguments containing `&`, `|`, `^` and `%` sent to a `.cmd` target (#2, cases C and D)
- `exposes` names that are invalid or try to leave the shims folder (#3)
- version ordering where text order and version order differ, such as `9.x` vs `20.x`
- refusing to delete when the shims folder is a reparse point (#3)
