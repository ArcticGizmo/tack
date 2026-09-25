@echo off
setlocal

:: Read version from Directory.Build.props if not passed as argument.
if not "%~1"=="" (
    set VERSION=%~1
) else (
    for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Select-Xml -Path Directory.Build.props -XPath \"//Version\").Node.InnerText"') do set VERSION=%%i
)

if "%VERSION%"=="" (
    echo Error: Could not determine version. Pass as argument: publish.bat 1.2.3
    exit /b 1
)

echo Building tack v%VERSION%...

:: The console CLI (tack.exe) - the Velopack mainExe, which hosts the install/update/uninstall hooks.
dotnet publish src\Tack.Cli\Tack.Cli.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded ^
    -o publish\

if %ERRORLEVEL% neq 0 (
    echo Build failed - tack.
    exit /b %ERRORLEVEL%
)

echo Publishing tack-shim (NativeAOT) ...

:: tack-shim is the per-tool proxy, copied under each tool name at reshim time. Published into the SAME dir
:: as tack.exe so Velopack packs both together. It runs on every tool call,
:: so NativeAOT gives the best cold start - but AOT needs the Visual Studio "Desktop development with C++"
:: workload for the native linker. When that's missing (common on a fresh dev box) the AOT publish can't
:: link, so fall back to a self-contained single-file build below so LOCAL packaging still works. CI
:: releases (release.yml, on a runner that has the workload) stay AOT.
dotnet publish src\Tack.Shim\Tack.Shim.csproj -c Release -r win-x64 -o publish\

if %ERRORLEVEL% neq 0 (
    echo.
    echo NativeAOT publish failed - falling back to a self-contained single-file tack-shim so local
    echo packaging can proceed. This local build has a slower shim cold-start than a CI/AOT release;
    echo install the C++ workload from https://aka.ms/nativeaot-prerequisites for an AOT-equivalent build.
    echo.
    dotnet publish src\Tack.Shim\Tack.Shim.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -p:EnableCompressionInSingleFile=true -o publish\
)

if %ERRORLEVEL% neq 0 (
    echo tack-shim publish failed.
    exit /b %ERRORLEVEL%
)

echo Packaging ...

:: --shortcuts None - tack is a console CLI, so no Desktop / Start Menu shortcuts.
vpk pack --packId Tack --packTitle "Tack" --packVersion %VERSION% --packDir publish\ --mainExe tack.exe --shortcuts None --outputDir releases\

if %ERRORLEVEL% neq 0 (
    echo Pack failed. Is the vpk CLI installed? Run: dotnet tool install -g vpk
    exit /b %ERRORLEVEL%
)

echo Writing checksums ...

:: Mirrors the SHA256SUMS.txt that release.yml publishes, so install.ps1 can be pointed at a local pack.
:: LF-terminated, lower-case hex, sha256sum's own format. Hashes EVERYTHING currently in releases\ except
:: the manifest itself (a local dir accumulates older versions' nupkgs, unlike CI's clean per-run set).
powershell -NoProfile -Command "$d = Resolve-Path 'releases'; $lines = Get-ChildItem -File -LiteralPath $d -Exclude 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }; [System.IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join [char]10) + [char]10); Write-Host ('  ' + @($lines).Count + ' files hashed')"

if %ERRORLEVEL% neq 0 (
    echo Checksum generation failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Release artifacts ready in: releases\
echo Upload to: https://github.com/ArcticGizmo/tack/releases/new?tag=v%VERSION%
echo   Include SHA256SUMS.txt -- install.ps1 refuses to install a release without it.
