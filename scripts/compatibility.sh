#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
export NUGET_PACKAGES="$PWD/artifacts/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export EVERGREEN_COMPAT_ROOT="$PWD/artifacts/compatibility/work"
mkdir -p "$EVERGREEN_COMPAT_ROOT"
python3 scripts/acquire-runtime-tests.py
dotnet restore tests/RuntimeCompatibility/RuntimeCompatibility.csproj --locked-mode --verbosity minimal
dotnet restore src/EvergreenFS.Rewriter/EvergreenFS.Rewriter.csproj --locked-mode --verbosity minimal
dotnet build tests/RuntimeCompatibility/RuntimeCompatibility.csproj --no-restore -t:Rebuild --verbosity minimal
dotnet build src/EvergreenFS.Rewriter --no-restore --verbosity minimal
output="$PWD/tests/RuntimeCompatibility/bin/Debug/net10.0"
results="$PWD/artifacts/compatibility"
rm -f "$results/summary.json" "$results/baseline.trx" "$results/memory.trx" "$results/sqlite.trx" "$results/native.trx"
dotnet src/EvergreenFS.Rewriter/bin/Debug/net10.0/EvergreenFS.Rewriter.dll --inventory > "$results/api-inventory.json"
cp "$output/RuntimeCompatibility.dll" "$results/baseline.dll"
trap 'cp "$results/baseline.dll" "$output/RuntimeCompatibility.dll"' EXIT
status=0
export EVERGREEN_PROVIDER=baseline
export EVERGREEN_COMPAT_EVIDENCE="$results/baseline-evidence.jsonl"
: > "$EVERGREEN_COMPAT_EVIDENCE"
dotnet test tests/RuntimeCompatibility --no-build --no-restore \
  --logger "trx;LogFileName=baseline.trx" --results-directory "$results" || status=1
dotnet src/EvergreenFS.Rewriter/bin/Debug/net10.0/EvergreenFS.Rewriter.dll \
  "$output/RuntimeCompatibility.dll" --closure
for provider in memory sqlite native; do
  export EVERGREEN_PROVIDER="$provider"
  export EVERGREEN_COMPAT_EVIDENCE="$results/$provider-evidence.jsonl"
  : > "$EVERGREEN_COMPAT_EVIDENCE"
  dotnet test tests/RuntimeCompatibility --no-build --no-restore \
    --logger "trx;LogFileName=$provider.trx" --results-directory "$results" || status=1
done
python3 scripts/compatibility-results.py
exit "$status"
