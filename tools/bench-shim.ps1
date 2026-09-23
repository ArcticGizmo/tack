# bench-shim.ps1
#
# Measures the overhead tack's shim adds to a tool invocation. Two numbers:
#
#   A) Shim fixed overhead: invoke an UNREGISTERED tool so the shim starts, reads resolved.json, resolves,
#      fails fast and exits WITHOUT spawning a child. This isolates the shim's own cost (process start +
#      runtime init + JSON read), which is the part tack controls and the part NativeAOT is meant to shrink.
#
#   B) End-to-end added cost: time (shim -> trivial .cmd) vs (that .cmd directly). The delta is what a user
#      pays for putting tack on PATH, versus calling the tool themselves.
#
# NOTE ON THE BUILD BEING MEASURED: pass -ShimDir a NativeAOT publish for the real number. If you point it
# at the framework-dependent build (the default), the fixed-overhead figure is a PESSIMISTIC UPPER BOUND -
# it includes managed runtime JIT/startup that NativeAOT removes. The script prints which kind it measured.
# release.yml runs this against the AOT publish on CI, which is where the real number comes from.
#
# ASCII only, no BOM.

[CmdletBinding()]
param(
    [string]$ShimDir,
    [int]$Iterations = 60,
    [int]$Warmup = 8
)

# Continue (not Stop): the fast-fail measurement below deliberately makes the shim write to stderr, and
# PS 5.1 would otherwise wrap that native stderr as a terminating error. Our own guards use explicit throw.
$ErrorActionPreference = 'Continue'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ShimDir) { $ShimDir = Join-Path $here '..\src\Tack.Shim\bin\Release\net10.0' }

$shimSrc = Join-Path $ShimDir 'tack-shim.exe'
if (-not (Test-Path $shimSrc)) { throw "tack-shim.exe not found in $ShimDir. Build or publish the shim first." }

# Detect build kind: a managed dll beside the host => framework-dependent; none => NativeAOT single file.
$isAot = -not (Test-Path (Join-Path $ShimDir 'tack-shim.dll'))
$kind = if ($isAot) { 'NativeAOT (real)' } else { 'framework-dependent (UPPER BOUND; AOT will be faster)' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("tack-bench-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    Copy-Item (Join-Path $ShimDir 'tack-shim.*') $work
    # Framework-dependent builds need the Tack.Core dependency beside the host; the AOT single-file has none.
    if (Test-Path (Join-Path $ShimDir 'Tack.Core.dll')) { Copy-Item (Join-Path $ShimDir 'Tack.Core.*') $work }
    $nodeExe  = Join-Path $work 'node.exe'    # registered -> full proxy path
    $ghostExe = Join-Path $work 'ghost.exe'   # unregistered -> fast-fail, no child spawn
    Copy-Item (Join-Path $work 'tack-shim.exe') $nodeExe
    Copy-Item (Join-Path $work 'tack-shim.exe') $ghostExe

    $targetCmd = Join-Path $work 'real-node.cmd'
    "@echo off`r`nexit /b 0`r`n" | Out-File -FilePath $targetCmd -Encoding ascii -NoNewline

    $resolved = Join-Path $work 'resolved.json'
    (@{ tools = @{ node = @{ default = $targetCmd } } } | ConvertTo-Json -Depth 6) | Out-File -FilePath $resolved -Encoding ascii
    $env:TACK_RESOLVED = $resolved

    # Drive children via the .NET Process API (not the PS call operator) so tiny stdout/stderr can't trip
    # PS 5.1's native-error handling, and timing covers exactly start -> exit.
    function Invoke-Once([string]$exe, [string]$argLine) {
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argLine
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $p = [Diagnostics.Process]::Start($psi)
        [void]$p.StandardOutput.ReadToEnd()
        [void]$p.StandardError.ReadToEnd()
        $p.WaitForExit()
        $p.Dispose()
    }

    function Measure-Call([string]$exe, [string]$argLine) {
        for ($i = 0; $i -lt $Warmup; $i++) { Invoke-Once $exe $argLine }
        $samples = New-Object 'System.Collections.Generic.List[double]'
        for ($i = 0; $i -lt $Iterations; $i++) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            Invoke-Once $exe $argLine
            $sw.Stop()
            $samples.Add($sw.Elapsed.TotalMilliseconds)
        }
        $sorted = @($samples | Sort-Object)
        [pscustomobject]@{
            Min    = [math]::Round($sorted[0], 2)
            Median = [math]::Round($sorted[[int]($sorted.Count / 2)], 2)
            Mean   = [math]::Round(($samples | Measure-Object -Average).Average, 2)
            Max    = [math]::Round($sorted[$sorted.Count - 1], 2)
        }
    }

    $shimFixed   = Measure-Call $ghostExe ''
    $throughShim = Measure-Call $nodeExe  ''
    $direct      = Measure-Call $env:ComSpec ('/c "' + $targetCmd + '"')

    $addedMedian = [math]::Round($throughShim.Median - $direct.Median, 2)
    $sizeKb = [math]::Round((Get-Item $shimSrc).Length / 1KB, 1)

    Write-Host ""
    Write-Host "=== tack-shim latency bench ===" -ForegroundColor Cyan
    Write-Host ("build measured : {0}" -f $kind)
    Write-Host ("shim exe size  : {0} KB" -f $sizeKb)
    Write-Host ("iterations     : {0} (warmup {1})" -f $Iterations, $Warmup)
    Write-Host ""
    Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f 'measurement', 'min', 'median', 'mean', 'max')
    Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f 'A. shim fixed overhead (no child)', $shimFixed.Min, $shimFixed.Median, $shimFixed.Mean, $shimFixed.Max)
    Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f 'B1. through shim -> .cmd', $throughShim.Min, $throughShim.Median, $throughShim.Mean, $throughShim.Max)
    Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f 'B2. .cmd directly (baseline)', $direct.Min, $direct.Median, $direct.Mean, $direct.Max)
    Write-Host ""
    Write-Host ("added cost of going through tack (median B1 - B2): {0} ms" -f $addedMedian) -ForegroundColor Yellow
    Write-Host ("all times in milliseconds, wall clock, per invocation")
    Write-Host ""
}
finally {
    Remove-Item Env:\TACK_RESOLVED -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
