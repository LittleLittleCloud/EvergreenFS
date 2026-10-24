# Managed transparency: scope and evidence

## Boundaries, not a universal interception claim

The build-time layer supports a broad managed subset of eight System.IO
types. It does not implement every member, native handle, reflection path,
platform convention or observable BCL implementation detail. Unsupported target
methods/constructors/fields are rejected before output replacement.

The exact overload inventory is [managed-api-inventory.json](managed-api-inventory.json).
On SDK 10.0.100, including inherited public methods:

| Type | Exact callable signatures | Reflected total |
|---|---:|---:|
| File | 72 | 109 |
| Directory | 35 | 54 |
| FileStream | 63 | 74 |
| FileInfo | 34 | 56 |
| DirectoryInfo | 43 | 63 |
| FileSystemInfo | 12 | 32 |
| StreamReader | 39 | 41 |
| StreamWriter | 81 | 83 |

These counts describe signature mapping, not a claim of 379 independently
validated behavioral contracts. Some signatures accept a constrained argument
subset and reject unsupported options explicitly at runtime.

### Streams and history

ManagedFileStream is a Stream implementation, not a FileStream subclass.
The rewriter changes application types as well as constructors and calls.
Native SafeFileHandle/Handle access, OS locks, handle constructors,
serialization and subclassing FileStream/StreamReader/StreamWriter/info types
are unsupported, not silently delegated to the host.

Path constructors enforce mode/access validity, missing parents, CreateNew,
read-only attributes and symmetric FileShare.Read/Write compatibility.
Exactly one writer is permitted per virtual path/provider even when callers
request FileShare.Write. Multiple readers work. Deleting/renaming an open
file or a directory containing one is explicitly rejected, **including
FileShare.Delete**; delete-pending inode semantics are not implemented.
Sharing is scoped to a provider instance and does not coordinate separate
providers/processes.
FileShare.Inheritable is rejected because no native descriptor exists to inherit.

Each handle has its own position and buffer. Dirty writer bytes are not in
revision snapshots or other readers until Flush or Dispose. Clean read
handles refresh committed bytes, so another handle observes a completed flush.
Create/truncate commits an empty-file mutation after open validation; later
dirty flush commits a content revision. Repeated clean flush adds none.
Whole-file File.WriteAllBytes/Text/Lines and append facades validate/encode
before publishing a single provider mutation rather than exposing an
intermediate empty revision. Sharing checks and the mutation share one
provider-specific lease gate. Direct IVirtualFileSystem calls are administrative
operations; do not mix them with active managed handles or assume they enforce
application sharing leases.
Cancellation before async IO causes no mutation. Async methods execute managed
buffer/provider IO synchronously and return Task/ValueTask; they are not
background disk IO, and cancellation does not roll back an already started
commit. Buffer size 0/1 and WriteThrough commit writes immediately, but do not
promise platform `fsync` durability.

Streams buffer complete file contents in memory; sparse/huge-file semantics,
OS-level buffer visibility, partial-write recovery and device IO are outside
scope. SetLength is buffered until flush. Failed flushes propagate and leave
history unpublished; disposal releases the lease even if flushing fails.
Callers must dispose streams before their provider.

### Metadata and enumeration

Supported metadata is Name/FullName/Extension, Exists, Length, directory
relationships, and ReadOnly/Hidden/Archive/Normal/Directory flags. Attribute
mutations are revisioned and preserved through snapshots, moves, copies,
SQLite reopen and forks. Creation/access/write timestamps, UnixFileMode,
ACLs, link targets, OS ownership and file identity are unsupported.
Hidden flags on new dotfiles and info object cache/Refresh/ToString behavior
are virtual semantics, not exact native caching behavior.

Wildcard matching uses the BCL FileSystemName matcher. SearchOption and
EnumerationOptions support recursive traversal, maximum depth,
AttributesToSkip, MatchType and MatchCasing. PlatformDefault casing is the
virtual namespace's ordinal case-sensitive default. Special dot directories
are rejected. Enumeration is eager/sorted; ordering, lazy exception timing,
IgnoreInaccessible traversal behavior and native case aliases are not exact
native equivalence. Mount/path normalization remains explicit.

Copy/move/replace are single provider mutations and single revisions. SQLite
stores the resulting tree and operation in one transaction. Original uses
the corresponding native operation and is not transactionally crash-safe.
Source/destination aliases and missing parents fail before publishing history.
Readonly move/delete behavior is intentionally conservative and can differ
from Unix unlink rules.

### Application closure and other IO

`--closure` walks ordinary assembly references found alongside the entry DLL.
It excludes System.*, Microsoft.*, SQLitePCLRaw.*, xunit* and the explicitly
trusted EvergreenFS/provider/rewriter/compatibility-fixture infrastructure,
plus System/mscorlib/netstandard/testhost/Newtonsoft.Json. These are trusted
name conventions, **not a hostile-code assembly identity policy**.
Missing application dependencies fail. Runtime-loaded dependencies and
reflection-created calls are not discovered.

Application fields, arrays, locals, signatures, generic bindings/constraints,
interfaces, async state machines and method operands are remapped. A concrete
IO type crossing an unrewritten trusted API boundary is rejected; Stream,
TextReader and TextWriter boundaries can use normal virtual dispatch.
Direct API inventory gaps produce EGFS001 with actionable signatures.
Unsafe function pointer/native/reflection mechanisms and other IO products
(RandomAccess, MemoryMappedFile, FileSystemWatcher, networking, P/Invoke,
BinaryReader/Writer path extensions in other libraries) are outside the
eight-type scope. BinaryReader/Writer given a virtual Stream are safe managed
consumers, not separately intercepted types.

The rewriter validates the entire closure before staging output. Each DLL
replacement is atomic, but a process/disk failure during replacing multiple
DLLs can leave mixed versions; rebuild before use. No concurrent rewrite,
signed assemblies, AOT, trimming, ReadyToRun, publish bundling or PDB rewriting
is promised.

## Upstream-derived tests

`tests/RuntimeCompatibility/Upstream` contains actual source bytes from
dotnet/runtime **60629d14374c56f1cb51819049ad1fa529307f8d** and its MIT license.
[`sources.json`](../tests/RuntimeCompatibility/sources.json) records every
original path, SHA-256, inclusion decision and exclusion reason.
`python3 scripts/acquire-runtime-tests.py` verifies bytes and checks that the
project compiles exactly the selected file set. `--acquire` reproducibly
downloads and verifies that same pin without changing hashes.

Eight entire files are compiled without altering any test/assertion:
ReadWriteAllText, ReadWriteAllTextAsync, ReadWriteAllLines, Append,
AppendAllBytes, FileInfo/CreateText, FileInfo/AppendText, FileStream/FlushAsync.
They exercise byte spans, text/encoding/BOM, lines, null/invalid/missing paths,
append/overwrite, read-only and sharing errors, async/cancellation, instance
APIs, real text writers and cross-handle flush visibility. Inherited theory
cases contribute many cases; this is not the full runtime filesystem suite.

Only infrastructure is local:
`tests/RuntimeCompatibility.Support/Fixtures.cs` supplies FileSystemTest paths,
owned cleanup, a 256-byte TestBuffer, platform capability predicates and a
ConditionalFact attribute. It installs the requested provider using explicit
administrative host paths. It is intentionally **not rewritten**, while all
test application IO is rewritten. The native baseline uses ordinary host
paths and no provider. Predicates evaluate platform/threading/privilege
conditions, not desired assertion outcomes; no included case is weakened,
filtered by method, or marked skipped to make it pass. Standard .NET analyzers
are disabled for unchanged upstream compilation (CA2022 flags an upstream
assertion's intentionally inexact Read); assertions are untouched.

The five other acquired test files are excluded **in their entirety**:
ReadWriteAllBytes (procfs/FIFO/pipes), FileStream ReadWriteSpan/Position
(derived/unseekable streams), FileInfo Length (symlinks/platform fixtures),
FileInfo Directory (OS root/UNC/trailing-path semantics). Their assertions
are not deleted, modified or reported as passing. Manifest entries give
specific reasons. File.Exists/Delete standalone upstream suites, broader
Directory suites and the rest of runtime were not acquired/executed. Local
dependency/regression tests separately cover deletion, copy/move/replace,
wildcards/recursion, lengths and mode/share/seek/disposal errors.

`bash scripts/compatibility.sh` rebuilds an original unrewritten assembly,
executes the native baseline, rewrites that same source assembly and runs
memory/sqlite/native in separate test processes. It restores the original
DLL afterward. Each target emits separate TRX and fixture evidence JSONL
with actual provider class, virtual path and history counts. The result
checker rejects zero executions, skips, failures, wrong providers, missing
virtual mutation evidence, or differing case sets. `summary.json` records
counts and per-case outcomes. No full runtime build was started.

Run `bash scripts/compatibility.sh` to produce the actual results and evidence
under ignored `artifacts/compatibility/`.
The optional full upstream native checkout runner remains separately **UNRUN**.
