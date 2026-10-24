#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:?Usage: bash scripts/pack.sh VERSION}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
  echo "VERSION must be an explicit NuGet SemVer, e.g. 0.1.0-ci.123.1" >&2
  exit 2
fi
export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
export NUGET_PACKAGES="$PWD/artifacts/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
output="$PWD/artifacts/nuget/$version"
mkdir -p "$output"
dotnet restore EvergreenFS.sln --locked-mode -p:Version="$version" --verbosity minimal
dotnet pack src/EvergreenFS/EvergreenFS.csproj --no-restore -c Release \
  -p:Version="$version" --output "$output" --verbosity minimal
dotnet pack src/EvergreenFS.Build/EvergreenFS.Build.csproj --no-restore -c Release \
  -p:Version="$version" --output "$output" --verbosity minimal
python3 scripts/inspect-packages.py "$version" "$output"
echo "Packages: $output"
