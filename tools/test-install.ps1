# test-install.ps1  -  regression suite for install.ps1 and the release pipeline.
#
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-install.ps1
#
# Ported from perch. It loads install.ps1's functions (without running the installer) and asserts the
# encoded "lessons": every pipeline file stays pure ASCII with no BOM and parses; the SHA256SUMS parser
# handles text- and binary-mode entries and fails closed; and the byte[]-vs-string manifest decode is
# actually required. ASCII only, no BOM.

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent $here

$script:pass = 0
$script:fail = 0
function Check([string]$name, [bool]$cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function CheckThrows([string]$name, [scriptblock]$sb) {
    try { & $sb | Out-Null; Check $name $false } catch { Check $name $true }
}

# --- Load install.ps1's functions without running the entrypoint or the #Requires directive. ---
$installPath = Join-Path $root 'install.ps1'
$src = Get-Content -Raw -LiteralPath $installPath
$src = $src -replace '(?m)^#Requires.*$', ''
$src = $src -replace '(?m)^Install-Tack\s+.*$', ''
Invoke-Expression $src

# --- 1. Encoding + parse across the whole release pipeline -----------------------------------------
Write-Host "`n[1] Encoding and parse (release pipeline is ASCII, BOM-free, parses)"
$pipeline = @(
    'install.ps1',
    'publish.bat',
    'run.bat',
    'tools\test-install.ps1',
    'tools\bench-shim.ps1',
    '.github\workflows\release.yml'
)
foreach ($rel in $pipeline) {
    $p = Join-Path $root $rel
    if (-not (Test-Path $p)) { Check "$rel exists" $false; continue }
    $bytes = [IO.File]::ReadAllBytes($p)
    $firstNonAscii = -1
    for ($i = 0; $i -lt $bytes.Length; $i++) { if ($bytes[$i] -gt 127) { $firstNonAscii = $i; break } }
    Check "$rel is pure ASCII" ($firstNonAscii -lt 0)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    Check "$rel has no BOM" (-not $hasBom)
    if ($rel -like '*.ps1') {
        $errs = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$null, [ref]$errs)
        Check "$rel parses cleanly" ($errs.Count -eq 0)
    }
}

# --- 2. Get-ExpectedHash: text + binary mode, fail-closed -------------------------------------------
Write-Host "`n[2] Get-ExpectedHash"
$h1 = 'a' * 64
$h2 = 'b' * 64
$sums = "$h1  Tack-win-Setup.exe`n$h2 *Tack-win-Portable.zip"
Check "text-mode entry resolves"   ((Get-ExpectedHash -Sums $sums -Name 'Tack-win-Setup.exe' -Tag v1) -eq ('A' * 64))
Check "binary-mode (*) entry resolves" ((Get-ExpectedHash -Sums $sums -Name 'Tack-win-Portable.zip' -Tag v1) -eq ('B' * 64))
CheckThrows "missing entry throws (fail-closed)" { Get-ExpectedHash -Sums $sums -Name 'not-listed.exe' -Tag v1 }
CheckThrows "an HTML 404 body throws"            { Get-ExpectedHash -Sums '<!DOCTYPE html><html>Not Found</html>' -Name 'Tack-win-Setup.exe' -Tag v1 }

# --- 3. Manifest byte[] vs string decode (the IWR .Content lesson) ---------------------------------
Write-Host "`n[3] Manifest decoding requires the UTF8 decode"
$manifestText  = "$h1  Tack-win-Setup.exe`n"
$manifestBytes = [System.Text.Encoding]::UTF8.GetBytes($manifestText)
$decoded = if ($manifestBytes -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($manifestBytes) } else { [string]$manifestBytes }
Check "decoded manifest resolves"  ((Get-ExpectedHash -Sums $decoded -Name 'Tack-win-Setup.exe' -Tag v1) -eq ('A' * 64))
# Skipping the decode stringifies the array to "97 97 ..." which matches no hash line:
CheckThrows "undecoded byte[] matches nothing" { Get-ExpectedHash -Sums ([string]$manifestBytes) -Name 'Tack-win-Setup.exe' -Tag v1 }

# --- 4. Get-AssetUrl --------------------------------------------------------------------------------
Write-Host "`n[4] Get-AssetUrl"
$release = [pscustomobject]@{
    tag_name  = 'v1'
    html_url  = 'https://github.com/ArcticGizmo/tack/releases/tag/v1'
    assets    = @([pscustomobject]@{ name = 'Tack-win-Setup.exe'; browser_download_url = 'https://example/download' })
}
Check "resolves a present asset" ((Get-AssetUrl -Release $release -Name 'Tack-win-Setup.exe') -eq 'https://example/download')
CheckThrows "throws for a missing asset (e.g. no SHA256SUMS.txt)" { Get-AssetUrl -Release $release -Name 'SHA256SUMS.txt' }

# --- 5. The real manifest publish.bat writes (only if a local pack exists) --------------------------
Write-Host "`n[5] Local releases\SHA256SUMS.txt (skipped if you haven't run publish.bat)"
$localSums = Join-Path $root 'releases\SHA256SUMS.txt'
if (Test-Path $localSums) {
    $mBytes = [IO.File]::ReadAllBytes($localSums)
    $mText  = [System.Text.Encoding]::UTF8.GetString($mBytes)
    $noBom  = -not ($mBytes.Length -ge 3 -and $mBytes[0] -eq 0xEF -and $mBytes[1] -eq 0xBB -and $mBytes[2] -eq 0xBF)
    Check "manifest has no BOM" $noBom
    Check "manifest is LF-only (no CR)" (-not ($mText -match "`r"))
    Check "manifest does not list itself" (-not ($mText -match '(?m)\s+SHA256SUMS\.txt\s*$'))
    Check "install.ps1 would accept the packed Setup" (
        [bool]((Get-ExpectedHash -Sums $mText -Name 'Tack-win-Setup.exe' -Tag local) -match '^[0-9A-F]{64}$'))
}
else {
    Write-Host "  (skip) run publish.bat first to validate the real manifest" -ForegroundColor DarkGray
}

Write-Host ""
if ($script:fail -gt 0) {
    Write-Host ("FAILED: {0} checks failed, {1} passed." -f $script:fail, $script:pass) -ForegroundColor Red
    exit 1
}
Write-Host ("OK: all {0} checks passed." -f $script:pass) -ForegroundColor Green
