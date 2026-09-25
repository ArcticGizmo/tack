@echo off
:: Dev convenience: run the tack CLI from source. e.g.  run.bat --version   |   run.bat open
:: %~dp0 anchors the project path to this script's folder, so it works from any directory.
dotnet run --project "%~dp0src\Tack.Cli" -c Debug -- %*
