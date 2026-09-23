# M1 spike - the shim, end to end

> **Superseded (2026-09-23):** the shim has graduated into `src/Tack.Shim` + `tests/Tack.Tests` (see
> `docs/m0-status.md`). This tree is kept as a frozen record of the viability spike; the `reach-test/`
> acceptance harness here still works. New shim work happens in `src/`, not here.


A throwaway-but-honest proof of the riskiest part of tack: a Windows exec-proxy that dispatches a bare
`node` call to the right binary for the current directory, correctly and fast enough to sit on every call.
See `../../docs/scope-and-implementation-plan.md` section 5 (the shim) and milestone **M1**.

> The plan's own words: *"If this milestone is ugly, everything else is premature."* This spike exists to
> find out whether it's ugly.

## What's here

| Path | Role |
| --- | --- |
| `src/Tack.Shim/` | The NativeAOT proxy (`tack-shim`). Identifies its tool from its own filename, resolves a target from `resolved.json`, spawns + proxies it. This is the real thing, kept minimal. |
| `src/Tack.StubTool/` | A fake tool (`tack-stub`) that reports which binary ran, its cwd, forwarded args, and stdin, and exits with a chosen code. |
| `tests/Tack.Shim.Tests/` | xUnit tests that run the **actual** shim exe (renamed per tool, as in production) against the stub and real `.cmd` files. |
| `bench/bench-shim.ps1` | Latency benchmark: shim fixed overhead, and the cost added over calling the tool directly. |

## Run it

```powershell
# correctness (fast; framework-dependent build, no AOT toolchain needed)
dotnet test m1-shim.slnx -c Release

# latency (pass an AOT publish dir for the real number; defaults to the framework-dependent build)
dotnet build m1-shim.slnx -c Release
powershell -ExecutionPolicy Bypass -File bench\bench-shim.ps1
```

### For the real (AOT) latency number

NativeAOT linking needs the **Desktop development with C++** workload (MSVC `link.exe`), which was not
installed on the spike machine. With it present:

```powershell
dotnet publish src\Tack.Shim\Tack.Shim.csproj -c Release -r win-x64 -o publish\shim
powershell -ExecutionPolicy Bypass -File bench\bench-shim.ps1 -ShimDir publish\shim
```

The bench auto-detects which build kind it measured and labels the output accordingly.

## Findings

See `../../docs/m1-spike-findings.md` for the verdict, the measured numbers, and the sharp edges found
(notably: `.cmd`/`.bat` targets must route through `cmd.exe` - a full path to a `.cmd` does **not** just
work under `UseShellExecute=false`, contrary to the scope-plan's note).
