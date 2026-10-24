import json
from pathlib import Path
import xml.etree.ElementTree as ET

directory = Path(__file__).resolve().parent.parent / "artifacts" / "compatibility"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = []
for provider in ("baseline", "memory", "sqlite", "native"):
    root = ET.parse(directory / f"{provider}.trx").getroot()
    counters = root.find("t:ResultSummary/t:Counters", ns)
    counts = {key: int(counters.get(key, "0")) for key in ("total", "executed", "passed", "failed", "notExecuted")}
    cases = sorted(
        (case.get("testName"), case.get("outcome"))
        for case in root.findall("t:Results/t:UnitTestResult", ns)
    )
    evidence = [json.loads(line) for line in (directory / f"{provider}-evidence.jsonl").read_text().splitlines()]
    expected_provider = {"baseline": "NativeBaseline", "memory": "InMemoryFileSystem", "sqlite": "SqliteFileSystem", "native": "OriginalFileSystem"}[provider]
    if not evidence or any(item["provider"] != expected_provider for item in evidence):
        raise SystemExit(f"Missing/wrong actual provider evidence for {provider}.")
    if provider != "baseline" and (not all(item["testDirectory"] == "/v/tests" for item in evidence) or
                                   not any(item["revisions"] > 2 for item in evidence)):
        raise SystemExit(f"Missing virtual mutation evidence for {provider}.")
    results.append({"provider": provider, **counts, "fixtures": len(evidence),
                    "observedProvider": expected_provider, "cases": cases})
(directory / "summary.json").write_text(json.dumps(results, indent=2) + "\n")
for result in results:
    print({key: value for key, value in result.items() if key != "cases"})
    if not result["executed"] or result["failed"] or result["notExecuted"]:
        raise SystemExit("Compatibility requires nonzero execution, no failures and no skipped cases.")
if any(result["cases"] != results[0]["cases"] for result in results[1:]):
    raise SystemExit("Native and virtual case sets/outcomes differ.")
