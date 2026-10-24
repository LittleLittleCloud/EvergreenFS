"""Verify vendored, unchanged upstream sources; --acquire downloads pinned bytes."""
import hashlib
import json
from pathlib import Path
import sys
import urllib.request
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent / "tests" / "RuntimeCompatibility"
manifest = json.loads((root / "sources.json").read_text())
selected = {entry["local"] for entry in manifest["files"] if entry["included"]}
compiled = {Path(item.attrib["Include"]).name for item in ET.parse(root / "RuntimeCompatibility.csproj").iter("Compile")}
if selected != compiled:
    raise SystemExit("Manifest and compiled upstream file sets differ.")
if sys.argv[1:] not in ([], ["--acquire"]):
    raise SystemExit("Usage: python3 scripts/acquire-runtime-tests.py [--acquire]")
for entry in manifest["files"]:
    path = root / "Upstream" / entry["local"]
    if sys.argv[1:] == ["--acquire"]:
        source = entry["path"] if entry.get("root") else manifest["base"] + entry["path"]
        url = f"https://raw.githubusercontent.com/dotnet/runtime/{manifest['commit']}/{source}"
        with urllib.request.urlopen(url) as response:
            content = response.read()
        if hashlib.sha256(content).hexdigest() != entry["sha256"]:
            raise SystemExit(f"Upstream checksum mismatch: {source}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    if hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
        raise SystemExit(f"Vendored checksum mismatch: {path}")
print(f"Verified {len(manifest['files'])} pinned upstream source/license files; "
      f"{sum(e['included'] for e in manifest['files'])} entire test files selected.")
