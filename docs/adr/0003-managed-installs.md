# ADR 0003: tack may download tools, when asked

- **Status:** Accepted (2026-10-02)
- **Deciders:** the maintainer
- **Changes:** the [scope plan](../scope-and-implementation-plan.md#non-goals-v1--deliberately-deferred)'s first
  non-goal ("No downloading Node/Python. tack registers existing installs.")
- **Plan:** [M10: managed installs](../m10-managed-installs-plan.md), with the
  [checkpoint 0 findings](../m10-spike-findings.md)

## Context

tack was built on "you install the versions, tack does the dispatch". In practice that means keeping nvm-windows
or fnm around only to fetch versions, while tack already does their dispatch better (it reaches IDEs and scheduled
tasks, explains itself, and pins without a repo file). Those tools also bring their own PATH entries and shell
hooks, which tack then has to report as shadowing it.

The scope plan always left room for this: "a future 'backends' feature could add this; it's a separable concern by
design." Checkpoint 0 showed that the official Node and Python builds unpack into any folder, run without any
registry entries, and publish SHA-256 values for their archives, so a backend can stay small.

## Decision

**`tack tool install <tool>@<spec>` downloads an official build, checks it, unpacks it into tack's own folder and
registers it, exactly as `tack tool add` would have.** It's opt-in and additive:

- **`tack tool add` is unchanged.** An install from anywhere (winget, an archive, nvm-windows, a company share) is
  still a first-class version. Both commands register through the same function, so a managed version is just a
  registered version that tack also happens to own the files of.
- **Only an explicit command downloads.** The shim never touches the network, and a `tack.yml` can never cause a
  download, however it's written. A repo can pin `node: 20`; only you can decide to fetch it.
- **Files live in `%LOCALAPPDATA%\tack\installs\<tool>\<version>\`,** next to tack's config. They're local, not
  roaming, survive `tack update`, and are kept by an uninstall like the rest of tack's data.
- **Integrity fails closed.** HTTPS only. The archive's SHA-256 must match the vendor's published value, otherwise
  nothing is unpacked or registered. That proves the bytes match what the vendor published; it isn't a
  signature (the hash comes from the same host), which is the same position `install.ps1` takes for tack itself.
- **tack only deletes what it installed:** a folder inside the installs root that carries tack's receipt file and
  is recorded as managed in config. A version registered with `tool add` is never deleted, whatever its path.
- **Supported sources:** Node from nodejs.org and Python from python.org (3.11 onwards, where python.org publishes
  hashed zips). Anything else is still `tool add`.

## Trust

Nothing here changes the [trust model](../design/dispatch-trust-model.md)'s position:

- **Downloads run as you, with no elevation, into folders only you can write.** That's T5's baseline (code running
  as you can overwrite a tool in your profile), shared with every per-user tool: fnm, scoop, nvm, pip's user
  installs. `tack doctor`'s folder-permission check covers `installs\` like the rest of tack's data.
- **Nothing new is visible to other accounts.** The installs folder isn't on any PATH. It's reached only through your
  shims, which read only your config (ADR 0002).
- **No new writes outside tack's own folders.** No registry entries (no PEP 514 keys for Python), no Start menu
  shortcuts, no PATH entries beyond the ones ADR 0002 already makes.
- **The new input is the network.** That's why the checks are fail-closed, why a repo can't trigger a download, and
  why the receipt records the URL and hash a version came from, for `tack info` to show.

## Alternatives considered

| Option | Why not |
|---|---|
| Keep the non-goal and document nvm-windows / fnm alongside tack | It's the friction this ADR exists to remove: two tools fighting over PATH, and two sets of state. |
| Auto-install a pinned version on first use, in the shim | Network and multi-second unpacks inside a tool call; a repo deciding what gets downloaded; and the shim's start-up budget. tack's explicit `tool install` keeps all of that out of the hot path. |
| Delegate to winget | Per-machine installs need admin (ADR 0002 rules that out), versions are coarse, and side-by-side versions aren't its model. |
| Shell out to fnm / uv for the downloading | A second tool to install and trust, with its own folders and PATH opinions, for a step that's an HTTP GET, a hash and an unzip. |
| python-build-standalone for Python | Relocatable and widely used (uv, mise), but third-party binaries. python.org's own zips work and are easier to defend to a security team. |

## Consequences

**Good:**

- One tool to fetch, pin and dispatch Node and Python, with no shell hooks and nothing on PATH but tack's shims.
- `doctor`, `info` and `list` can say where a managed version came from.

**Accepted:**

- tack now makes network calls (only from `tool install` and `tool available`) and needs the system proxy to work.
- A managed Python can't be moved: pip's launchers embed their absolute path, and venvs made from it record it.
  Removing it breaks those venvs, and `tool remove` says so.
- Python before 3.11 can't be installed by tack: python.org publishes no hash for those builds. `tool add` still
  takes them.
- Commands added later (`npm i -g`, `pip install`) aren't shimmed until `tack reshim` rescans the managed
  versions. Nothing runs it automatically yet (see §5.6 of the scope plan).
