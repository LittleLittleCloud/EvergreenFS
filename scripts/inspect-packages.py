"""Check the exact release artifacts, without trusting project build outputs."""
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

version, directory = sys.argv[1:]
ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
for package in ("EvergreenFS", "EvergreenFS.Build"):
    path = Path(directory) / f"{package}.{version}.nupkg"
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        root = ET.fromstring(archive.read(f"{package}.nuspec"))
        # NuGet may select a different schema version for runtime metadata.
        namespace = root.tag.split("}")[0].lstrip("{")
        ns = {"n": namespace}
        metadata = root.find("n:metadata", ns)
        assert metadata.findtext("n:version", namespaces=ns) == version
        assert metadata.findtext("n:license", namespaces=ns) == "MIT"
        assert "Xiaoyun Zhang" in metadata.findtext("n:authors", namespaces=ns)
        for required in ("README.md", "LICENSE", "THIRD-PARTY-NOTICES.txt",
                         "licenses/Mono.Cecil-MIT.txt", "licenses/dotnet-MIT.txt",
                         "licenses/Apache-2.0.txt"):
            assert required in names, (path, required)
        assert not any(name.startswith(("artifacts/", "src/", "tests/")) or
                       name.endswith((".log", ".trx", ".pdb", ".cs")) or
                       "prompt" in name.lower() or "handoff" in name.lower()
                       for name in names), names
        for name in names:
            data = archive.read(name)
            assert b"/Users/" not in data and b"/home/runner/" not in data, name
        if package == "EvergreenFS":
            assert "lib/net10.0/EvergreenFS.dll" in names
            assert not any(name.startswith(("tools/", "build/")) for name in names)
        else:
            assert not any(name.startswith(("lib/", "ref/")) for name in names)
            assert "build/EvergreenFS.Build.targets" in names
            for file in ("EvergreenFS.Rewriter.dll", "EvergreenFS.Rewriter.deps.json",
                         "EvergreenFS.Rewriter.runtimeconfig.json", "Mono.Cecil.dll",
                         "EvergreenFS.dll", "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll"):
                assert f"tools/net10.0/{file}" in names, file
            deps = json.loads(archive.read("tools/net10.0/EvergreenFS.Rewriter.deps.json"))
            assert f"EvergreenFS/{version}" in deps["libraries"]
            dependency = metadata.find("n:dependencies/n:group/n:dependency", ns)
            assert dependency.attrib["id"] == "EvergreenFS"
            assert dependency.attrib["version"] == f"[{version}]"
        print(f"PASS {path.name}: {len(names)} entries")
        for name in names:
            print(f"  {name}")
