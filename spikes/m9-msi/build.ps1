<#
.SYNOPSIS
    Builds two versions of the MSI spike (1.0.0 and 1.0.1) as per-machine Velopack MSIs into .\releases.
    Build only: nothing is installed. See README.md for the runbook.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Start from a clean feed so the 1.0.1 delta is built against this run's 1.0.0.
foreach ($d in 'publish', 'releases', 'msi') {
    $p = Join-Path $here $d
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

foreach ($v in '1.0.0', '1.0.1') {
    $pub = Join-Path $here "publish\$v"
    dotnet publish (Join-Path $here 'TackMsiSpike.csproj') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:Version=$v -o $pub
    if ($LASTEXITCODE -ne 0) { throw "publish $v failed" }

    vpk pack --packId TackMsiSpike --packTitle 'Tack MSI Spike' --packAuthors ArcticGizmo --packVersion $v `
        --packDir $pub --mainExe tack-msi-spike.exe --shortcuts None `
        --msi --instLocation PerMachine --outputDir (Join-Path $here 'releases')
    if ($LASTEXITCODE -ne 0) { throw "vpk pack $v failed" }

    # vpk writes the MSI under one fixed name, so each version's copy is kept before the next pack overwrites it.
    $msiDir = New-Item -ItemType Directory -Force (Join-Path $here 'msi')
    Copy-Item (Join-Path $here 'releases\TackMsiSpike-win.msi') (Join-Path $msiDir "TackMsiSpike-$v.msi") -Force
}

Get-ChildItem (Join-Path $here 'releases'), (Join-Path $here 'msi') | Select-Object Name, Length
