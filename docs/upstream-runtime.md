# Full upstream native runtime runner (UNRUN)

**Full runtime build/suite status: UNRUN.** This document concerns the optional
full upstream checkout/native build only. The separate
[source-based compatibility harness](managed-transparency.md#upstream-derived-tests)
now actually executes unchanged selected runtime sources natively and through
rewriting with all three providers. Its results are not full-suite results.
The original EvergreenFS regression tests remain locally authored.

The following is a separate, reproducible upstream **native baseline** runner,
pinned to the `dotnet/runtime` **v10.0.0** release commit
**`60629d14374c56f1cb51819049ad1fa529307f8d`**, not a moving branch.
Acquisition and the upstream build download dependencies and require network
access and the upstream platform prerequisites. This was not executed here.
The upstream repository's own `global.json` selects its build SDK; that can
differ from this project's installed 10.0.100.

The supervisor independently checked the official release metadata and
source: the class in
[`File/ReadWriteAllBytes.cs`](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Runtime/tests/System.IO.FileSystem.Tests/File/ReadWriteAllBytes.cs)
is **`System.IO.Tests.File_ReadWriteAllBytes`**. The pinned
[upstream testing instructions](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/docs/workflow/testing/libraries/testing.md)
document `/p:XUnitOptions="-class Test.ClassUnderTests"`.

The helper has separate acquisition and execution modes. Neither was executed
as part of this MVP validation. From this repository root:

```sh
bash scripts/upstream-runtime.sh acquire
# Separately, only when intentionally preparing an upstream native build:
(cd artifacts/runtime-v10.0.0 && ./build.sh clr+libs -c Release)
# Run just the selected native test class after prerequisites are built:
bash scripts/upstream-runtime.sh run
```

`acquire` clones the release tag and **verifies HEAD equals the exact hash**;
an existing mismatched or tracked-dirty checkout is rejected. `run` verifies
it again and requires an existing upstream `artifacts/bin/testhost`; it does
not launch a full runtime build. That explicit prerequisite build above may
be substantial and **has not been run here**.

The run command uses the documented filter:

```sh
./dotnet.sh build src/libraries/System.Runtime/tests/System.IO.FileSystem.Tests/System.IO.FileSystem.Tests.csproj \
  -c Release -t:Test \
  /p:XUnitOptions="-class System.IO.Tests.File_ReadWriteAllBytes -xml /absolute/path/results.xml"
```

It selects the read/write byte test class, including theory cases. The helper
requires Python 3 to parse the generated xUnit XML, rejects missing XML, zero
executed tests (including all-skipped runs), failed tests and runner errors,
and propagates build/test failure. Logs and XML go to a fresh
`artifacts/upstream-results.XXXXXX/` directory to avoid accepting stale results.
This runner is documented and syntax-checked, **not an executed upstream
test result**.

This optional full-checkout runner runs upstream against native System.IO,
**not EvergreenFS**. The selected source-based harness is instead run with
`bash scripts/compatibility.sh`. Its sources, lightweight fixture adapters,
source exclusions and separate provider results are described in the linked
document. Neither runner establishes full runtime compatibility.

To reproduce the actual provider differential harness in this MVP:

```sh
dotnet test tests/EvergreenFS.Tests --filter FullyQualifiedName~LocallyAuthoredDifferentialCompatibility
```

All three providers run the same byte/text/encoding, existence, parent,
directory, enumeration, deletion, and scoped exception trace against the
machine's native baseline. Ordering is normalized. This does not establish
universal runtime or cross-platform compatibility.
