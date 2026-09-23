# setup-reach-test.ps1  (M1 SPIKE - acceptance harness)
#
# Builds an INERT test harness that proves tack's core claim: a shim on the persisted USER PATH is reached
# by processes that were NOT launched from your shell and never sourced a shell profile (Visual Studio, the
# Task Scheduler service, an Explorer-launched window). This script only writes files and copies build
# output - it does NOT modify PATH, the registry, or create any scheduled task. Those machine-state changes
# are left for you to do by hand (see the instructions this prints), so nothing here trips CrowdStrike.
#
# ASCII only, no BOM.

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$spike = Split-Path -Parent $here                       # ...\spikes\m1-shim
$shimBuild = Join-Path $spike 'src\Tack.Shim\bin\Release\net10.0'

if (-not (Test-Path (Join-Path $shimBuild 'tack-shim.exe'))) {
    throw "Build first: dotnet build `"$spike\m1-shim.slnx`" -c Release"
}

# Clean, recreate layout.
$dirs = @{
    root    = $here
    shims   = Join-Path $here 'shims'
    targets = Join-Path $here 'targets'
    projB   = Join-Path $here 'projB'
    proof   = Join-Path $here 'proof'
    vs      = Join-Path $here 'VsReachTest'
}
foreach ($d in $dirs.Values) { if ($d -ne $here) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } }
foreach ($d in $dirs.Values) { New-Item -ItemType Directory -Path $d -Force | Out-Null }

# 1. shims\ : the dir that goes on PATH. Copy the shim payload, then a copy named node.exe. (Framework-
#    dependent build - fine for a reach test; latency is a separate concern. The renamed host still loads
#    tack-shim.dll + reads tack-shim.runtimeconfig.json from this dir.)
foreach ($f in @('tack-shim.exe','tack-shim.dll','tack-shim.runtimeconfig.json','tack-shim.deps.json')) {
    $src = Join-Path $shimBuild $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $dirs.shims $f) -Force }
}
Copy-Item (Join-Path $dirs.shims 'tack-shim.exe') (Join-Path $dirs.shims 'node.exe') -Force

# 2. targets\ : distinct markers so the proof says exactly which binding won and from what cwd.
$mk = @{
    'default.cmd' = 'DEFAULT'
    'projB.cmd'   = 'PROJB-BINDING'
    'vs.cmd'      = 'VS-PREBUILD'
}
foreach ($name in $mk.Keys) {
    $which = $mk[$name]
    "@echo off`r`necho TACK-REACH-OK which=$which cwd=%CD% argv=%*`r`n" |
        Out-File -FilePath (Join-Path $dirs.targets $name) -Encoding ascii -NoNewline
}

# 3. resolved.json BESIDE the shim (so the shim finds it with no TACK_RESOLVED env set - as a real
#    scheduler task / VS build would have). default + two directory bindings.
$resolved = @{
    tools = @{
        node = @{
            default = (Join-Path $dirs.targets 'default.cmd')
            dirs    = @{
                (Convert-Path $dirs.projB) = (Join-Path $dirs.targets 'projB.cmd')
                (Convert-Path $dirs.vs)    = (Join-Path $dirs.targets 'vs.cmd')
            }
        }
    }
}
($resolved | ConvertTo-Json -Depth 8) | Out-File -FilePath (Join-Path $dirs.shims 'resolved.json') -Encoding ascii

# 4. Explorer double-click harness: opens a console (launched by Explorer, with Explorer's profile env),
#    runs a BARE `node` resolved via PATH, from inside projB so the projB binding wins.
$dc = @"
@echo off
cd /d "$($dirs.projB)"
echo Running a bare 'node' from a window Explorer launched (not your shell)...
node --version
echo.
echo If you see TACK-REACH-OK above, the shim was reached via PATH.
pause
"@
$dc | Out-File -FilePath (Join-Path $here 'double-click-me.cmd') -Encoding ascii

# 5. Scheduler task XML (import with schtasks or the Task Scheduler GUI). Runs a bare `node` via cmd, cwd
#    = projB, capturing output to proof\scheduler-proof.txt. The task's process env is composed by the
#    scheduler service from your user profile - the faithful stand-in for "a process tack didn't launch".
$user = "$env:USERDOMAIN\$env:USERNAME"
$proofFile = Join-Path $dirs.proof 'scheduler-proof.txt'
$taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>tack M1 spike - proves the shim is reached from a non-shell, profile-env process.</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>cmd.exe</Command>
      <Arguments>/c node --version &gt; "$proofFile" 2&gt;&amp;1</Arguments>
      <WorkingDirectory>$($dirs.projB)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
$taskXml | Out-File -FilePath (Join-Path $here 'tack-spike-reach.task.xml') -Encoding unicode

# 6. Visual Studio project: a trivial console app whose PRE-BUILD event runs a bare `node`. Building it in
#    VS runs that event with devenv's env (which never sourced your shell), from the project dir - bound to
#    vs.cmd. The marker shows up in the Build Output window.
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <!-- The acceptance test: a bare 'node' run by Visual Studio's build, resolved via PATH -> tack shim. -->
    <PreBuildEvent>node --version</PreBuildEvent>
  </PropertyGroup>
</Project>
"@ | Out-File -FilePath (Join-Path $dirs.vs 'VsReachTest.csproj') -Encoding ascii

@"
// Building this in Visual Studio fires the PreBuildEvent (a bare 'node'). Watch the Build Output window for
// a line 'TACK-REACH-OK which=VS-PREBUILD ...' - that is Visual Studio hitting the tack shim via PATH.
System.Console.WriteLine("VsReachTest built.");
"@ | Out-File -FilePath (Join-Path $dirs.vs 'Program.cs') -Encoding ascii

Write-Host ""
Write-Host "Harness ready under: $here" -ForegroundColor Green
Write-Host ""
Write-Host "ADD THIS ONE DIRECTORY TO THE TOP OF YOUR USER 'Path' (see instructions):" -ForegroundColor Cyan
Write-Host ("    " + $dirs.shims) -ForegroundColor Yellow
Write-Host ""
Write-Host "Proof file the scheduler task writes: $proofFile"
