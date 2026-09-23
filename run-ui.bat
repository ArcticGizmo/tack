@echo off
:: Dev convenience: launch the tack desktop UI (tack-ui) from source, isolated from any installed tack.
:: A Debug build runs against the "tack (Dev)" data space (see TackProfile), so it never touches an
:: installed release's config, shims or PATH. Equivalent to `run.bat open` once the UI is built.
dotnet run --project src\Tack.App -c Debug -- %*
