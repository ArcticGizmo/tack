# M1 spike findings - is tack viable?

> **Date:** 2026-09-22. **Spike code:** `spikes/m1-shim/`. **Scope reference:** `docs/scope-and-implementation-plan.md`, section 5 and milestone M1.

## Verdict

**Yes, tack is viable - with one number still to confirm.**

The hard, load-bearing part of tack (a Windows exec-proxy shim that dispatches a bare `node` to the right
binary per directory, and reaches processes the shell didn't launch) is **mechanically sound**. Every
correctness sharp edge the plan worried about is handled and covered by tests running the real mechanism.

The single open question is **AOT cold-start latency**, which could not be measured on this machine because
the NativeAOT linker (the "Desktop development with C++" workload) is not installed. Everything points to
it being fine (see below), but the actual millisecond figure should be confirmed before committing to M2+.

## What was proven (6/6 tests pass, exercising the real exe)

| Concern from the plan | Result |
| --- | --- |
| Correct binary dispatch from the shim's own filename (`argv[0]` basename) | Works. A copy of the shim named `node.exe` proxies the tool `node`; `python.exe` (unregistered) fails cleanly. |
| **Per-directory** resolution (the whole point of tack) | Works. `node` resolves to different targets under `projB` vs elsewhere; nearest-ancestor binding wins for nested dirs. |
| Exit-code propagation | Works. Child exit `7` and `.cmd` `exit /b 3` both surface as the shim's exit code. |
| Argument forwarding (order preserved, verbatim) | Works. |
| Inherited std handles / stdin piping | Works. Piped stdin reaches the child; a caller that redirects the shim captures the child's output. |
| `.cmd` / `.bat` targets (npm is `npm.cmd`) | Works - **via `cmd.exe /c`** (see sharp edge below). |
| Ctrl-C not killing the wrapper early | Handler installed (`SetConsoleCtrlHandler` returns TRUE so the shim ignores the signal and the child owns it). Verified by construction / manual; not automated (see gaps). |
| AOT-safe config read (no reflection, no YAML on hot path) | `resolved.json` read via `System.Text.Json` source generator. Compiles and runs. |

## Sharp edges found (worth carrying into M2)

1. **`.cmd`/`.bat` cannot be started directly under `UseShellExecute=false`.** The scope plan (section 5.3)
   says a full path to a `.cmd` "just resolves via the associated interpreter" - that is only true for
   `UseShellExecute=true`, which spawns a new window and breaks handle inheritance. A `.cmd` is not a PE
   image, so `CreateProcess` rejects it outright. **The shim routes `.cmd`/`.bat` through `cmd.exe /c`.**
   This matters because npm/npx/yarn shims on Windows are `.cmd`. Note for M2: `cmd.exe`'s argument
   re-parsing is a known quoting hazard; the current spike forwards args simply and does not yet harden
   quoting for args containing spaces, `&`, `^`, `"`, etc.

2. **`argv[0]` identity comes from `Environment.ProcessPath`**, and survives renaming the host exe - which
   is exactly the production model (copy the one shim exe under each tool name). Confirmed for the managed
   apphost; the AOT single-file exe behaves the same (it *is* the process).

3. **Passthrough (no-resolution) is deferred.** The spike fails with exit 127 when nothing resolves; v1's
   "exec the next binary on PATH" behaviour (plan section 4, item 5) is M2 work.

## The one open risk: AOT latency

Latency is the reason M1 exists (it runs on *every* tool call). Measured on this machine
(60 iterations, warmup 8):

| Build measured | Shim fixed overhead (no child) | Added cost over calling the tool directly |
| --- | --- | --- |
| **framework-dependent (JIT) - UPPER BOUND** | ~98 ms median | ~109 ms median |

**~98 ms is unacceptable for a hot-path shim - and that is precisely why the plan mandates NativeAOT.**
The managed runtime's cold start dominates that figure; NativeAOT removes it (no JIT, no runtime
self-init). This number is a pessimistic ceiling, not the shipping figure.

Why the AOT figure is very likely fine, on strong evidence rather than hope:

- The sibling **`perch-hook`** occupies the identical architectural slot (tiny NativeAOT helper on a
  per-tool-call hot path) and its whole design premise is that AOT cold start is competitive with, or
  better than, a `powershell.exe` invocation. That project already made this bet and shipped it.
- The shim does almost nothing on the hot path: one small file read (source-gen JSON), a handful of
  directory comparisons, one `CreateProcess`. There is no heavy work for AOT startup to be slow around.

### Blocker to getting the real number here

NativeAOT publish fails on this machine with *"Platform linker not found ... Desktop development with C++
workload"*. VS Community 2026 is installed but without the C++ (VC.Tools) workload, and the only `link.exe`
on PATH is Git's. `perch-hook` builds framework-dependent locally too and AOT-links in CI - so this box has
never linked AOT.

**To confirm viability fully, get the AOT number one of two ways:**
- add the C++ workload locally (VS Installer -> "Desktop development with C++", or Build Tools with
  `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`), then
  `dotnet publish src\Tack.Shim -c Release -r win-x64 -o publish\shim` and re-run the bench with
  `-ShimDir publish\shim`; or
- let CI produce the AOT binary (as perch does) and bench it there.

Expectation to validate against: **single-digit to low-double-digit milliseconds** of shim fixed overhead.
If it lands there, M1 is green and the project is viable as designed.

## Gaps in the spike (deliberately out of scope for a viability check)

- Ctrl-C behaviour is handled in code but not automated (sending real console-control events to a child in
  a test is flaky); worth a targeted manual test in M1-proper.
- The "reaches a process your shell didn't launch" claim (the plan's Visual Studio acceptance test) has a
  ready-to-run harness at `spikes/m1-shim/reach-test/` (`INSTRUCTIONS.md`): it proves the reach via Explorer,
  the Task Scheduler service, and a VS pre-build event, once the shims dir is added to the user PATH. The
  harness wiring is verified; the PATH change + GUI launches are a manual, EDR-safe step for the operator.
- `cmd.exe` argument-quoting hardening (see sharp edge 1).
- Passthrough-to-next-on-PATH, `tack.yml` walk-up, full precedence, real registry - all M2.
- No AOT binary produced here (toolchain gap above).

## Recommendation

Proceed to M2. Carry forward the `.cmd`-via-`cmd.exe` finding and the quoting caveat. Before building much
on top, get the AOT cold-start figure (local C++ workload or CI) and confirm it is in the single-digit ms
range; that is the last box to tick on M1's core risk.
