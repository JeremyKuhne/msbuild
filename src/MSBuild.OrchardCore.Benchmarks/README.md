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

## Cross-binary semantic manifest

Generate an untimed canonical JSON manifest before comparing benchmark binaries:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore `
  --semantic-manifest C:\results\orchard-full-shared.json `
  --evaluation-stage Full --sharing-policy Shared
```

The manifest preserves solution and item evaluation order while sorting unordered
property, target, and metadata collections. Machine-specific Orchard Core, SDK, MSBuild,
NuGet, and temporary roots are replaced with stable tokens. Generate it twice in fresh
identically launched processes for the same binary and require byte-identical output
before using it as a cross-binary correctness oracle.

Use a checked JSON project subset for representative cohorts without changing benchmark
code. Paths are relative to the Orchard Core root and must name unique MSBuild projects in
`OrchardCore.slnx`:

```json
{
  "schemaVersion": 1,
  "projects": [
    "src/OrchardCore/OrchardCore.Rules.Core/OrchardCore.Rules.Core.csproj",
    "src/OrchardCore/OrchardCore.Resources/OrchardCore.Resources.csproj"
  ]
}
```

Pass the file with `--project-subset <path>` to repository evaluation, semantic manifest,
or evaluation profile modes.

Use `--repository-measurement <path>` for a one-shot JSON measurement of the exact
repository cohort, stage, and sharing policy. It records operation wall-clock ticks,
process-wide allocated bytes, project count, and checksum without serializing evaluated
items inside the measured interval.

## Evaluation profiling

Generate an evaluation profile through the same in-process project collection, stage, and
sharing policy as the repository benchmark:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore `
  --evaluation-profile C:\results\orchard-items-shared.md `
  --evaluation-stage Items --sharing-policy Shared
```

Profiling is diagnostic and intentionally runs outside BenchmarkDotNet iterations.
Add `--wildcard-trace <path>` to a one-shot manifest, profile, or graph-measurement
command to capture the existing FileMatcher filespec, selected driver, and fallback
diagnostics. This option enables `MSBUILDLOGEXPANDEDWILDCARDS` and
`MSBUILDENABLEDEBUGTRACING` before loading MSBuild and installs a process-local trace
listener; it must not be used for retained timings.

Profile production-style graph construction by adding `--project-graph`. Graph profiles
always use full evaluation with the graph's shared evaluation context. Omit
`--degree-of-parallelism` for the logical-core default or specify a positive integer:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore `
  --project-graph --evaluation-profile C:\results\orchard-graph.tsv
```

## Project graph construction

Use `--project-graph` to benchmark the normal production graph path with a fresh
`ProjectCollection` per operation. The two methods compare degree one with the default
logical-core parallelism:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore --project-graph --job short
```

BenchmarkDotNet's memory diagnoser counts only the invoking thread, so it is not used as
the allocation gate for this parallel workload. Capture a process-wide allocated-byte
delta and deterministic graph topology in a fresh process instead:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-repository C:\src\OrchardCore `
  --project-graph-measurement C:\results\orchard-graph-default.json `
  --degree-of-parallelism default
```

## Single-project command-line evaluation

The original benchmark repeatedly invokes the MSBuild command-line query path for one
project:

```powershell
.\.dotnet\dotnet.exe run --project src\MSBuild.OrchardCore.Benchmarks -c Release -- `
  --orchard-core-project C:\src\OrchardCore\src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj `
  --job short
```