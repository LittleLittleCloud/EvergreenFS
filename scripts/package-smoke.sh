#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
repo="$PWD"
version="${1:?Usage: bash scripts/package-smoke.sh VERSION [PACKAGE_DIRECTORY]}"
feed="${2:-$repo/artifacts/nuget/$version}"
feed="$(cd "$feed" && pwd)"
python3 scripts/inspect-packages.py "$version" "$feed"
mkdir -p artifacts
work="$(mktemp -d "$repo/artifacts/package smoke.XXXXXX")"
export DOTNET_CLI_HOME="$work/cli home"
export NUGET_PACKAGES="$work/isolated packages"
export NUGET_HTTP_CACHE_PATH="$work/http cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cp -R packaging/smoke/App packaging/smoke/Dependency "$work/"
cp global.json "$work/"
# Stop repository-wide MSBuild settings from participating in this independent consumer.
printf '<Project />\n' > "$work/Directory.Build.props"
printf '<Project />\n' > "$work/Directory.Build.targets"
mkdir -p "$work/local feed"
cp "$feed/EvergreenFS.$version.nupkg" "$feed/EvergreenFS.Build.$version.nupkg" "$work/local feed/"
cat > "$work/NuGet.Config" <<'XML'
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="local feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="EvergreenFS" /><package pattern="EvergreenFS.Build" /><package pattern="EvergreenSmoke.Dependency" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML
cd "$work"
dotnet pack Dependency/Dependency.csproj -c Release -p:SmokeVersion="$version" -o "local feed" --verbosity minimal
dotnet restore App/App.csproj -p:SmokeVersion="$version" --use-lock-file --verbosity minimal
dotnet restore App/App.csproj -p:SmokeVersion="$version" --locked-mode --verbosity minimal
for mode in initial incremental rebuild; do
  target=Build
  if [ "$mode" = rebuild ]; then target=Rebuild; fi
  dotnet build App/App.csproj -c Release --no-restore -t:"$target" -p:SmokeVersion="$version" --verbosity minimal
  for provider in memory sqlite; do
    mount="$work/virtual root $mode $provider"
    dotnet App/bin/Release/net10.0/App.dll "$provider" "$mount" "$work/$mode.db"
    if [ -e "$mount" ]; then echo "FAIL: virtual root leaked to host: $mount" >&2; exit 1; fi
  done
  python3 - "$version" "$mode" <<'PY'
import hashlib
import json
from pathlib import Path
import sys
version, mode = sys.argv[1:]
output = Path("App/bin/Release/net10.0")
deps = json.loads((output / "App.deps.json").read_text())
assert f"EvergreenFS/{version}" in deps["libraries"], "Runtime absent from deps.json"
assert (output / "EvergreenFS.dll").is_file(), "Runtime absent from output"
original = Path("isolated packages/evergreensmoke.dependency") / version / "lib/net10.0/Dependency.dll"
assert original.read_bytes() != (output / "Dependency.dll").read_bytes(), "Dependency not rewritten"
hashes = {name: hashlib.sha256((output / name).read_bytes()).hexdigest()
          for name in ("App.dll", "Dependency.dll", "EvergreenFS.dll")}
record = Path("initial-hashes.json")
if mode == "initial":
    record.write_text(json.dumps(hashes))
else:
    assert hashes == json.loads(record.read_text()), f"Non-idempotent {mode}: {hashes}"
print(f"PASS {mode}: runtime in deps.json/output; package dependency rewritten; hashes repeat-safe")
PY
done
# The packaged tool must itself be runnable without the source-tree tool output.
dotnet "$NUGET_PACKAGES/evergreenfs.build/$version/tools/net10.0/EvergreenFS.Rewriter.dll" --inventory > inventory.json
echo "PASS standalone Release package consumer. Evidence: $work"
