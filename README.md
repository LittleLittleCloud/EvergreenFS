# EvergreenFS

.NET 10 executable virtual filesystem and **build-time managed System.IO
interception**. Ordinary application code and its application dependencies can
use File/Directory, FileInfo/DirectoryInfo, FileStream, and path-based
StreamReader/StreamWriter APIs without calling providers. This is a substantial
managed expansion, **not universal transparent interception or a security
sandbox**. Unsupported overloads are rejected, not redirected to host IO.

## Packages

Requires .NET 10 (repository/CI SDK: **10.0.100**). Intended packages are
`EvergreenFS` (runtime and providers) and `EvergreenFS.Build` (portable build-time
rewriter, with an exact matching runtime dependency). Publication is pending
NuGet account/trusted-publisher setup; no published version is claimed here.

Once a CI version is available:

```sh
dotnet add package EvergreenFS.Build --version 0.1.0-ci.RUN_NUMBER.RUN_ATTEMPT
```

Replace the version with an actual run's version and set `PrivateAssets="all"`
on the build PackageReference. Normal Build/Rebuild automatically rewrites the
application DLL and intended application dependency closure; no manual imports
or repository paths are needed. [Installation, local pack/smoke reproduction,
CI jobs and automatic main-push NuGet setup](docs/packaging.md).

For trusted publishing, configure a GitHub Actions repository or `nuget`
environment Secret named `NUGET_USER` containing the NuGet profile username,
not an API key or email.

## Build and actually run compatibility

Requires SDK **10.0.100**, Python 3, and NuGet access on the first restore.

```sh
bash scripts/verify.sh
# Just the unchanged upstream-source matrix:
bash scripts/compatibility.sh
# Verify vendored source hashes, or reacquire exact pinned source bytes:
python3 scripts/acquire-runtime-tests.py
python3 scripts/acquire-runtime-tests.py --acquire
```

`verify.sh` performs locked restore, build, local regression tests, the original
isolated System.IO sample, CLI demo, and upstream-derived compatibility matrix.
CLI home, package cache and results stay in `artifacts/`. Solution and compatibility projects include
NuGet lockfiles. For deliberate dependency changes, run an unlocked restore,
review the lockfile changes, then rerun locked verification.

The upstream-derived suite uses eight **unchanged entire source files** from
dotnet/runtime commit `60629d14374c56f1cb51819049ad1fa529307f8d`, plus separately
identified infrastructure adapters. The native baseline is built unrewritten;
the same test assembly is then rewritten and executed with InMemory, SQLite
and Original. No full runtime build is required.

Latest macOS arm64/SDK 10.0.100 run: **51 local tests passed**; **623 upstream
cases passed per target** (native baseline and three providers), with **0
failures and 0 skips**. Clean build: **0 warnings/errors**.

Actual results, exact source provenance, API coverage and boundaries:
[managed transparency](docs/managed-transparency.md).
The optional [full upstream native build](docs/upstream-runtime.md) remains
**UNRUN**; it is distinct from the source-based tests that actually run here.

## Architecture

`IVirtualFileSystem` provides byte operations, directories, attributes, atomic
copy/move/replace, history, snapshots, and forks. Three interchangeable
providers implement it:

| Provider | Construction | Persistence |
|---|---|---|
| InMemory | `new InMemoryFileSystem("/v")` | Managed memory |
| SQLite | `new SqliteFileSystem("state.db", "/v")` | Full immutable state per revision, metadata/history across reopen |
| Original/native | `new OriginalFileSystem("/owned/root", "/v")` | Real files under the explicit physical root; revision history is session-local |

`VirtualFileSystem.Use(provider)` selects an `AsyncLocal` provider scope.
Dispose nested scopes in LIFO order and own the provider's lifetime explicitly.
No active scope means an exception, **never implicit native fallback**.
Managed info objects and open streams capture their provider at construction.

`FileFacade`/`DirectoryFacade` and managed instance replacements implement the
application surface. `ManagedFileStream` derives from **Stream**, not
FileStream: there is no inherited FileStream implementation that can open the
host. StreamReader/Writer replacements derive from their BCL counterparts and
feed them virtual streams, retaining their real encoding/buffering/async text
behavior. Safe BCL calls on Stream, TextReader, and TextWriter use virtual
dispatch; native-handle APIs are rejected.

## Exact API inventory and supported families

[`docs/managed-api-inventory.json`](docs/managed-api-inventory.json) is the
reflection-based inventory of every public method/constructor on the eight
target types, compared with exact remapped return/parameter signatures.
Inherited public methods are included. `supported` means an exact callable
managed implementation exists, **not that every possible platform condition,
argument combination or upstream test has been validated**. Fields are
separately listed and unsupported. Generate the current inventory with:

```sh
dotnet src/EvergreenFS.Rewriter/bin/Debug/net10.0/EvergreenFS.Rewriter.dll --inventory
```

| Surface | Implemented managed families | Explicit gaps |
|---|---|---|
| File | bytes/text/lines; spans and memory; append; sync/async; open/create/read/write/text; copy/move/replace; attributes | native handles, times, links, Unix modes, encryption, async line iterators |
| Directory | create/delete/move, parent/root, files/directories/entries, wildcard and recursive/options overloads | process current directory, drives, times, links, Unix modes, special dot directories |
| FileInfo/FileSystemInfo | constructor, name/full path/extension, existence, length, attributes/read-only, refresh, directory, open/text/copy/move/replace/delete | timestamps, links, Unix modes, native ACLs, serialization |
| DirectoryInfo | constructor/create/subdirectory/parent/root, existence, move/delete, typed enumeration/options | timestamps, links, ACLs, OS root/UNC semantics |
| FileStream | path constructors and FileStreamOptions subset; byte/span/memory IO; sync/async; seek/position/length/setlength; flush/dispose; Stream APIs | native handles, lock/unlock, inheritance, DeleteOnClose, preallocation, multiple writers |
| StreamReader/Writer | ordinary path/stream constructors; inherited real text APIs | path+FileStreamOptions constructors and static IO fields |

Read the [detailed boundary catalog](docs/managed-transparency.md) before
using this with existing applications.

## Rewriter and dependencies

The samples reference the rewriter as a build dependency and import
`build/EvergreenFS.targets`. After Build it invokes:

```sh
dotnet src/EvergreenFS.Rewriter/bin/Debug/net10.0/EvergreenFS.Rewriter.dll \
  samples/ManagedApp/bin/Debug/net10.0/ManagedApp.dll --closure
```

The rewriter scans application references beside the target, excludes trusted
BCL/provider/tool/test infrastructure, validates all intended application
assemblies, and rewrites methods, instance constructors, fields, local
variables, arrays, generic signatures/constraints, properties, interfaces and
compiler-generated async types. Missing nontrusted dependencies and unsafe
typed BCL boundaries fail explicitly. No framework/provider internals are
rewritten. Repository samples use project references/imports; package consumers
use `EvergreenFS.Build` as described above.

Unsupported target calls/fields produce **EGFS001**, identifying the offending
method/signature/IL offset where applicable. Validation failure leaves every
input untouched. Successful output is staged and each DLL atomically replaced;
the closure replacement is **not one filesystem-wide atomic transaction**.
Rewriting again is repeat-safe. Signed assemblies and deriving from intercepted
IO types are rejected. Publish/trim/AOT/ReadyToRun/single-file and PDB remapping
are not supported. Reflection/dynamic invocation, external loading, native code
and APIs outside the eight-type inventory are **not intercepted**.

`samples/TransparentApp` remains the small ordinary System.IO isolation demo.
`samples/ManagedApp` calls a separate `ManagedDependency` assembly containing
instance APIs, generic FileInfo signatures, async methods, streams, text
constructors and recursive enumeration. Its end-to-end cases run with every
provider in the local regression suite.

## Paths, history and isolation

Absolute virtual paths must equal the mount or begin with `mount + "/"`;
`/v2` is not inside `/v`. Relative paths resolve from the **mount root**,
never the process working directory. Dot segments are normalized; escaping
the mount is rejected. Virtual names use slash separators and ordinal casing.
NUL, colon and backslash are rejected. Repeated/trailing slashes normalize;
this deliberately differs from exact Windows/Unix path syntax.
Enumeration returns sorted absolute virtual names, not host paths.

Revision 0 is initial state. Successful mutations append history; failures and
no-op mkdir/missing-file deletes do not. Stream creation/truncation is a
successful mutation; buffered writes publish a revision on dirty flush or
disposal, not on each Write. A failed open creates no history. Full-state
snapshots, data and attributes are defensively copied. A memory or persistent
SQLite fork starts independent history at revision 0 without ancestor branch
metadata. SQLite commits each mutation and its history atomically. Its API is
append-only, not resistant to external SQL tampering.

One provider instance serializes its operations. SQLite instances cache state;
a stale writer fails rather than overwriting another revision. Reopen to
refresh; no merges/distributed locking are implemented. Original serves reads
from its session tree and owns its physical root exclusively: no external
modification or overlapping providers. Reopen imports disk into a fresh
revision 0. Native disk changes and session history are not crash-atomic.
All snapshots are full copies, with no pruning or content deduplication.

SQLite's database filename and Original's root are trusted administrative
**host paths**, distinct from virtual names. InMemory/SQLite never resolve a
virtual path through host directories. Original rejects symlinks on scan and
checks mutation ancestors, but this is **not race-free containment**: hard
links, concurrent link replacement, special files, case aliases and hostile
host changes are outside scope. Use an exclusively owned ordinary-file root.
No in-process interception scheme here confines untrusted code; use OS
isolation for that.

## CLI explorer

```sh
CLI=src/EvergreenFS.Cli/bin/Debug/net10.0/EvergreenFS.Cli.dll
dotnet "$CLI" demo
dotnet "$CLI" artifacts/explorer.db mkdir /v/notes
dotnet "$CLI" artifacts/explorer.db write /v/notes/hello.txt first
dotnet "$CLI" artifacts/explorer.db write /v/notes/hello.txt second
dotnet "$CLI" artifacts/explorer.db tree
dotnet "$CLI" artifacts/explorer.db list /v/notes
dotnet "$CLI" artifacts/explorer.db history
dotnet "$CLI" artifacts/explorer.db read /v/notes/hello.txt
dotnet "$CLI" artifacts/explorer.db read /v/notes/hello.txt 2
dotnet "$CLI" artifacts/explorer.db fork 2 artifacts/fork.db
```

Use fresh database paths for those revision numbers. `tree` lists all paths;
`list [path]` lists immediate children (default `/v`). CLI read/write is UTF-8
text. The CLI uses provider/facade APIs explicitly; the separate samples prove
ordinary System.IO rewriting.
