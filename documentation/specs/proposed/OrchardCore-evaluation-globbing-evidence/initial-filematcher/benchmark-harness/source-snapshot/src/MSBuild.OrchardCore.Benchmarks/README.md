# Orchard Core benchmarks

These benchmarks use a restored [Orchard Core](https://github.com/OrchardCMS/OrchardCore)
checkout as a real-world MSBuild evaluation workload.

## Repository-scale evaluation

Restore the Orchard Core solution, then pass the repository root to the benchmark:

```powershell
dotnet restore C:\src\OrchardCore\OrchardCore.slnx
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore --job short
```

One measured operation sequentially evaluates every MSBuild project listed in
`OrchardCore.slnx`. Solution parsing and result validation happen during global setup and
are not timed. A fresh `ProjectCollection` and `EvaluationContext` are created for each
operation, so state does not leak between benchmark iterations.

The benchmark covers two evaluation stages:

- `Items`: evaluates through the item and lazy-item passes.
- `Full`: also evaluates using tasks and registers targets.

Each stage covers three cache policies:

- `Isolated`: no state is shared between project evaluations.
- `SharedSdkCache`: shares SDK resolution but gives each project fresh filesystem and glob
  caches. This is the baseline for each stage.
- `Shared`: shares SDK resolution, filesystem, directory-entry, and glob caches across the
  complete solution pass.

Global setup verifies that all policies produce the same ordered project, property, item,
and target identities. The benchmark evaluates each top-level project once; it does not
execute targets or construct target-framework inner builds.

Use BenchmarkDotNet filters to select a stage or method:

```powershell
# Higher-sample item-stage comparison
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore --filter "*Items*" `
  --iterationCount 10 --warmupCount 5 --launchCount 1

# Fast validation of one case (global setup still validates the full matrix)
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore --job Dry `
  --filter "*ItemsSharedSdkCache*"
```

## Single-project command-line evaluation

The original benchmark repeatedly invokes the MSBuild command-line query path for one
project:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-project C:\src\OrchardCore\src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj `
  --job short
```