# EvergreenFS

.NET 10 versioned virtual filesystems: InMemory, SQLite and Original/native,
with build-time interception of supported ordinary managed System.IO calls.
Requires .NET SDK 10.0.100 (or a compatible .NET 10 SDK) to build and .NET 10
to run. Both packages use the same version.

Install EvergreenFS.Build in the application using an ordinary PackageReference:

```xml
<PackageReference Include="EvergreenFS.Build" Version="YOUR_VERSION" PrivateAssets="all" />
```

It brings the exact matching EvergreenFS runtime transitively. The build package
contains build assets and a portable tool, not a lib assembly. PrivateAssets
prevents build integration leaking into downstream consumers; the application
still receives runtime assets. Libraries exposing EvergreenFS in their public API
should also reference EvergreenFS directly at the same version.

```csharp
using EvergreenFS;
using var fs = new InMemoryFileSystem("/v");
using var scope = VirtualFileSystem.Use(fs);
Directory.CreateDirectory("/v/notes");
await File.WriteAllTextAsync("/v/notes/hello.txt", "virtual");
Console.WriteLine(new FileInfo("/v/notes/hello.txt").Length);
Console.WriteLine(fs.History.Count);
```

Normal Build/Rebuild rewrites the output DLL and intended application dependency
closure beside it, including copied package dependencies. Incremental builds are
repeat-safe. Design-time builds are excluded. Framework/provider/tool dependencies
are excluded by the existing rewriter trust rules. Missing application dependencies,
unsupported IO APIs and signed assemblies fail explicitly. Use this on applications
whose dependency closure you own and can rewrite, not arbitrary third-party code.

This is not universal interception or a security sandbox. Reflection, dynamic
loading, native IO, publish/trim/AOT/ReadyToRun/single-file and PDB remapping are
not supported. No active provider scope means an exception, not host IO fallback.
Full limitations and API inventory: https://github.com/LittleLittleCloud/EvergreenFS

EvergreenFS.Build bundles the portable rewriter and its dependencies. See
THIRD-PARTY-NOTICES.txt and licenses/. The runtime's SQLite dependencies are
resolved by NuGet. Package availability depends on successful NuGet publishing;
the repository does not imply that a version has already been published.
