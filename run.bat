@echo off
:: Dev convenience: run the tack CLI from source. e.g.  run.bat --version   |   run.bat open
dotnet run --project src\Tack.Cli -c Debug -- %*
