# Packages and automatic CI publishing

The intended public repository is https://github.com/LittleLittleCloud/EvergreenFS.
Packages are **not claimed to be published**. NuGet publication cannot succeed
until the owner completes the account/trusted-publisher setup below.

## Install

Build and runtime require .NET 10; the repository and CI use SDK **10.0.100**.
For a version actually available on your chosen feed:

```sh
dotnet add package EvergreenFS.Build --version 0.1.0-ci.RUN_NUMBER.RUN_ATTEMPT
```

Replace the version with a real CI run's version. Set `PrivateAssets="all"` on
the resulting build PackageReference:

```xml
<PackageReference Include="EvergreenFS.Build" Version="0.1.0-ci.123.1" PrivateAssets="all" />
```

This package pulls **exactly the same version** of EvergreenFS transitively.
The runtime DLL is copied into application output and listed in `.deps.json`;
PrivateAssets does not suppress runtime assets in that application. It keeps
build integration from leaking to downstream projects. A library exposing runtime
types to consumers should reference EvergreenFS directly at the same version:

```sh
dotnet add package EvergreenFS --version 0.1.0-ci.123.1
```

EvergreenFS contains the providers/facades plus NuGet SQLite dependencies.
EvergreenFS.Build contains `build/EvergreenFS.Build.targets`, a portable
framework-dependent published rewriter (including deps/runtimeconfig and managed
and native dependencies), and license notices. It has no `lib`/`ref` assembly.
Only these two repository projects are packable.

Normal Build/Rebuild runs the bundled tool on the existing target DLL with
`--closure`. Every build visits the output because dependency DLLs may have been
recopied independently of the application. The rewriter is repeat-safe; it
validates the whole intended application closure before replacing any DLL.
Design-time and outer multi-targeting builds are excluded. Relative paths and
paths with spaces work without repository imports or source-tree outputs.

The tool preserves its existing exclusions for framework/provider/tool/test
infrastructure. It does not rewrite dependencies under those trusted name
prefixes. Missing nontrusted dependencies, unsupported intercepted APIs, unsafe
typed framework boundaries and signed assemblies fail explicitly. The package
does not promise arbitrary third-party compatibility, full publish integration,
trimming/AOT/ReadyToRun/single-file, or PDB remapping. See
[managed transparency](managed-transparency.md). Rewriting is not a sandbox.

## Local reproduction

Requires Bash, Python 3, .NET SDK 10.0.100 and initial NuGet network access.
Nothing here pushes packages:

```sh
bash scripts/verify.sh
bash scripts/pack.sh 0.1.0-ci.123.1
bash scripts/package-smoke.sh 0.1.0-ci.123.1
# Or validate packages downloaded elsewhere:
bash scripts/package-smoke.sh 0.1.0-ci.123.1 /absolute/path/to/feed
```

Packing does a locked solution restore and Release pack, publishes the portable
tool, and inspects both nupkg manifests/files. Packages are under
`artifacts/nuget/VERSION/`. Explicit versions make inputs reproducible; ZIP
timestamps/package hashes are not promised bit-for-bit reproducible.

The smoke script creates a fresh independent consumer in `artifacts/package smoke.*`,
with its own CLI home, empty global package and HTTP caches, MSBuild settings,
NuGet configuration and local feed. Source mapping requires the exact EvergreenFS
packages and a freshly packed independent dependency package to come from that
feed. There are no ProjectReferences or manual imports to this repository.
The Release app uses ordinary File/Directory/FileInfo/FileStream/async IO in both
its own assembly and a package dependency, verifies real provider data and past
revisions with memory/SQLite, verifies no physical virtual root, and verifies the
runtime is copied and in `.deps.json`. Initial/incremental/Rebuild executions
must agree and output DLL hashes must be identical; package dependency bytes
must differ from its original unrewritten package. It also runs the packaged
tool directly. Evidence remains locally under ignored `artifacts/`.

## Workflow

`.github/workflows/ci-release.yml` runs for pull requests, every main push and
manual dispatch. Main releases do **not** need tags.

1. `test`: Linux and macOS locked restore, build, local regression/samples/CLI,
   plus all selected unchanged upstream-source cases on native, memory, SQLite
   and Original. TRX, exact case summary, API inventory, provider evidence and
   logs are retained when present, even on failures.
2. `pack`: after both test jobs, Release pack/inspection on Linux. Uploads
   `nuget-packages` before any authentication step.
3. `package-smoke`: both Linux/macOS download and consume those exact nupkg
   artifacts in isolation, including native SQLite assets on the consumer OS.
4. `publish`: only after both smoke jobs, only a trusted push or dispatch on
   `LittleLittleCloud/EvergreenFS` main. Downloads the same artifact, obtains a
   short-lived OIDC NuGet key, and pushes runtime followed by build integration
   to `https://api.nuget.org/v3/index.json` with `--skip-duplicate`.

Every run/attempt uses **`0.1.0-ci.RUN_NUMBER.RUN_ATTEMPT`** consistently for
both packages and the bundled tool. These are intentionally prereleases, not
stable 0.1.0 releases. Reruns have new versions. A partially failed publish may
leave the runtime on the feed without the build package; the next run produces
a new matching pair. There is no cross-package atomic publish on NuGet.
Workflow concurrency never cancels an in-progress publication. No GitHub Release
is created: the automatic release deliverables are NuGet packages and Actions
artifacts.

## Owner setup (pending; not performed by implementation)

Use [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
and [NuGet/login v1](https://github.com/NuGet/login).

Create a public `LittleLittleCloud/EvergreenFS` repository and a GitHub environment
named **`nuget`**. Restrict it to main, but **do not configure required reviewers
or wait timers**: every main push must publish automatically.

In NuGet.org, sign in with the account that will own these packages, then create
a GitHub Trusted Publishing policy with this exact tuple:

| Field | Value |
|---|---|
| Repository owner | `LittleLittleCloud` |
| Repository | `EvergreenFS` |
| Workflow file (filename only) | `ci-release.yml` |
| Environment | `nuget` |
| Package patterns | `EvergreenFS` and `EvergreenFS.Build` |
| Scopes | Allow new packages and new versions (both IDs are initially unpublished) |

If the UI needs separate policies for exact package patterns, use the same tuple
for both. Choose the correct NuGet policy/package owner and ensure the publishing
account has ownership/permissions. Verify IDs are available; this implementation
does not reserve them.

Configure a GitHub Actions repository or `nuget` environment **Secret**
`NUGET_USER` containing the NuGet **profile username**, not an API key or email.
Both the setup check and NuGet login read this Secret only. The workflow fails
with an actionable error when it is absent; authentication failures remain
failures. It does not use a
permanent API-key fallback. Only the publish job has `id-token: write`; default
permissions are `contents: read`. Action revisions are pinned to verified tag
SHAs. The temporary key is used from `steps.login.outputs.NUGET_API_KEY` without
printing it. No account setup, credentials, external publication or repository
administration is performed by the local scripts.

After setup, push main or dispatch this workflow on main. Until NuGet login and
both pushes actually succeed, **no NuGet publication is established**, even if
the downloadable package artifacts and local smoke checks pass.
