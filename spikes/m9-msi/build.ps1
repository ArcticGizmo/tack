<#
.SYNOPSIS
    Builds two versions of the MSI spike (1.0.0 and 1.0.1) as per-machine Velopack MSIs, plus per-user
    Setup.exe copies, into .\installers and a local update feed in .\releases.
    Build only: nothing is installed. See README.md for the runbook.

.PARAMETER Velopack
    The Velopack version under test, for both the NuGet library and vpk. vpk runs as a local tool pinned in
    .config\dotnet-tools.json, so the global vpk is never touched. 1.2.0 is what tack pins; CI installs the
    latest vpk (1.2.158 at the time of the spike).
#>
param([string] $Velopack = '1.2.158')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Start from a clean feed so the 1.0.1 delta is built against this run's 1.0.0.
foreach ($d in 'publish', 'releases', 'installers') {
    $p = Join-Path $here $d
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

Push-Location $here
try {
    if (-not (Test-Path (Join-Path $here 'dotnet-tools.json'))) { dotnet new tool-manifest --output . | Out-Null }
    dotnet tool update vpk --local --version $Velopack
    if ($LASTEXITCODE -ne 0) { throw "could not install vpk $Velopack as a local tool" }

    foreach ($v in '1.0.0', '1.0.1') {
        $pub = Join-Path $here "publish\$v"
        dotnet publish (Join-Path $here 'TackMsiSpike.csproj') -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:Version=$v -p:VelopackVersion=$Velopack -o $pub
        if ($LASTEXITCODE -ne 0) { throw "publish $v failed" }

        # --runtime matters: vpk 1.2.158 otherwise defaults the package to x86 (Program Files (x86)).
        dotnet vpk pack --runtime win-x64 --packId TackMsiSpike --packTitle 'Tack MSI Spike' --packAuthors ArcticGizmo --packVersion $v `
            --packDir $pub --mainExe tack-msi-spike.exe --shortcuts None `
            --msi --instLocation PerMachine --outputDir (Join-Path $here 'releases')
        if ($LASTEXITCODE -ne 0) { throw "vpk pack $v failed" }

        # vpk writes the installers under fixed names, so each version's copies are kept before the next pack
        # overwrites them: the per-machine MSI, and the per-user Setup.exe (today's install, for the migration test).
        $out = New-Item -ItemType Directory -Force (Join-Path $here 'installers')
        Copy-Item (Join-Path $here 'releases\TackMsiSpike-win.msi') (Join-Path $out "TackMsiSpike-$v.msi") -Force
        Copy-Item (Join-Path $here 'releases\TackMsiSpike-win-Setup.exe') (Join-Path $out "TackMsiSpike-$v-Setup.exe") -Force
    }
}
finally {
    Pop-Location
}

Get-ChildItem (Join-Path $here 'releases'), (Join-Path $here 'installers') | Select-Object Name, Length
