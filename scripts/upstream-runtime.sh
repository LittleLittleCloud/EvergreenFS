#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
repository="$PWD/artifacts/runtime-v10.0.0"
commit=60629d14374c56f1cb51819049ad1fa529307f8d
test_class=System.IO.Tests.File_ReadWriteAllBytes

verify_checkout() {
  if [ ! -d "$repository/.git" ] ||
     [ "$(git -C "$repository" rev-parse HEAD)" != "$commit" ]; then
    echo "Expected dotnet/runtime checkout at $commit. Run acquire first." >&2
    exit 1
  fi
  if [ -n "$(git -C "$repository" status --porcelain --untracked-files=no)" ]; then
    echo "Upstream tracked files have changed; refusing an unpinned run." >&2
    exit 1
  fi
}

case "${1:-}" in
  acquire)
    mkdir -p artifacts
    if [ ! -e "$repository" ]; then
      git clone --depth 1 --branch v10.0.0 \
        https://github.com/dotnet/runtime.git "$repository"
    fi
    verify_checkout
    printf '%s\n' "$commit" > artifacts/runtime-v10.0.0.commit
    echo "Verified upstream checkout $commit. No build or tests executed."
    ;;
  run)
    verify_checkout
    command -v python3 >/dev/null || { echo "python3 is required to validate xUnit results." >&2; exit 1; }
    # Require a prior explicit upstream build; never start a full runtime build here.
    if [ ! -d "$repository/artifacts/bin/testhost" ]; then
      echo "Upstream testhost is missing. See docs/upstream-runtime.md for build prerequisites." >&2
      exit 1
    fi
    results=$(mktemp -d "$PWD/artifacts/upstream-results.XXXXXX")
    export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
    export NUGET_PACKAGES="$PWD/artifacts/packages"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
    (
      cd "$repository"
      ./dotnet.sh build \
        src/libraries/System.Runtime/tests/System.IO.FileSystem.Tests/System.IO.FileSystem.Tests.csproj \
        -c Release -t:Test \
        "/p:XUnitOptions=-class $test_class -xml $results/results.xml"
    ) 2>&1 | tee "$results/run.log"
    python3 - "$results/results.xml" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

path = pathlib.Path(sys.argv[1])
if not path.is_file():
    sys.exit("FAIL: missing xUnit XML; no upstream success established.")
root = ET.parse(path).getroot()
assemblies = [root] if root.tag == "assembly" else root.findall(".//assembly")
total = sum(int(a.get("total", "0")) for a in assemblies)
skipped = sum(int(a.get("skipped", "0")) for a in assemblies)
failed = sum(int(a.get("failed", "0")) + int(a.get("errors", "0")) for a in assemblies)
if total <= skipped or failed or root.findall(".//error"):
    sys.exit(f"FAIL: total={total}, skipped={skipped}, failures/errors={failed}; require executed tests and no failures.")
print(f"Native upstream baseline PASS: total={total}, skipped={skipped}, failed={failed}")
PY
    echo "Results: $results"
    ;;
  *)
    echo "Usage: bash scripts/upstream-runtime.sh acquire|run" >&2
    exit 2
    ;;
esac
