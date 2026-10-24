#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
export NUGET_PACKAGES="$PWD/artifacts/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
dotnet restore EvergreenFS.sln --locked-mode --verbosity minimal
dotnet build EvergreenFS.sln --no-restore --verbosity minimal
dotnet test EvergreenFS.sln --no-build --no-restore --verbosity minimal \
  --logger "trx;LogFileName=evergreen.trx" --results-directory "$PWD/artifacts/test-results"
dotnet run --project samples/TransparentApp --no-build --no-restore
dotnet run --project src/EvergreenFS.Cli --no-build --no-restore -- demo
bash scripts/compatibility.sh
