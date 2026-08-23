# Orchard Core performance investigation handoff

This document is the entry point for continuing the Orchard Core evaluation and
`FileMatcher` investigation on another Windows machine. The full findings and experiment
ledger are in the [investigation report](./OrchardCore-evaluation-globbing-performance.md).

## Pinned state

| Role | Commit |
| --- | --- |
| Investigation branch | `perf/orchardcore-globbing` |
| Investigation base and new A/B control | `05f089128b4b06b2c6a489a0602c18a53b18de38` |
| Retained FileMatcher candidate | `ed7d5756496954cd5399a3340cf663de115db50b` |
| Evolved benchmark harness | `981c4a4b2c6ba3916be2717fc3b71ab03637a6b0` |
| Historical #14663 parent | `288f4a653261b160f15fe079c39598e7b26d62b9` |
| Historical #14663 target (`T0`) | `65f75f9249a01a3ec8e5220f5173eba3b5db7bc5` |
| Orchard Core | `6a28ae14c64aedcf5c9c748d602fed8696f1e153` |

The retained candidate has two product changes: direct traversal checks the reparse-point
attribute before resolving a directory link target, and optimized callback traversal can
publish typed file/directory cache entries from one lexical-path-preserving snapshot. The
rejected broader file-only and recursive-no-exclude routes are not present.

Use `05f089...`, the parent of `ed7d575...`, as the control for new measurements of the
retained change. Use `65f75f...` only to reproduce the historical `T0` evidence.

## Fresh machine setup

```powershell
git clone https://github.com/jeremykuhne/msbuild.git
Set-Location msbuild
git switch perf/orchardcore-globbing

.\build.cmd -v quiet
$msbuildRoot = (Get-Location).Path
$orchardRoot = Join-Path (Split-Path $msbuildRoot -Parent) "OrchardCore-6a28ae1"
.\scripts\Prepare-OrchardCoreInvestigation.ps1 -Destination $orchardRoot
```

The setup script clones only from the public Orchard Core repository, checks out the exact
commit detached, requires a clean checkout, and restores `OrchardCore.slnx` with this
repository's SDK. It is safe to rerun against the same clean checkout. Use `-SkipRestore`
only when verifying an already restored copy.

## Smoke checks

```powershell
$dotnet = Join-Path $msbuildRoot ".dotnet\dotnet.exe"
$benchmarkProject = "src\MSBuild.OrchardCore.Benchmarks"
$results = Join-Path $msbuildRoot "artifacts\tmp\orchard-investigation"
New-Item $results -ItemType Directory -Force | Out-Null

& $dotnet run --project $benchmarkProject -c Release -- `
  --orchard-core-repository $orchardRoot --job Dry `
  --filter "*ItemsSharedSdkCache*"

& $dotnet run --project $benchmarkProject -c Release -- `
  --orchard-core-repository $orchardRoot `
  --repository-measurement (Join-Path $results "repository-items-shared.json") `
  --evaluation-stage Items --sharing-policy Shared

& $dotnet run --project $benchmarkProject -c Release -- `
  --orchard-core-repository $orchardRoot `
  --project-graph-measurement (Join-Path $results "graph-default.json") `
  --degree-of-parallelism default
```

Generate a semantic manifest twice in fresh processes before comparing binaries. The two
hashes must match:

```powershell
$manifest1 = Join-Path $results "full-shared-1.json"
$manifest2 = Join-Path $results "full-shared-2.json"

& $dotnet run --project $benchmarkProject -c Release -- `
  --orchard-core-repository $orchardRoot --semantic-manifest $manifest1 `
  --evaluation-stage Full --sharing-policy Shared
& $dotnet run --project $benchmarkProject -c Release -- `
  --orchard-core-repository $orchardRoot --semantic-manifest $manifest2 `
  --evaluation-stage Full --sharing-policy Shared

if ((Get-FileHash $manifest1).Hash -cne (Get-FileHash $manifest2).Hash) {
    throw "Fresh semantic manifests differ."
}
```

For an evaluation profile, replace the one-shot output option with
`--evaluation-profile <path>`. Add `--project-graph` for the production graph path. Add
`--wildcard-trace <path>` only to a one-shot diagnostic run; tracing invalidates timing.
The [benchmark README](../../../src/MSBuild.OrchardCore.Benchmarks/README.md) documents
all modes and project subsets.

## Isolated A/B worktrees

Apply the harness-only commit to the candidate's parent so both arms run identical
instrumentation while differing only in product code:

```powershell
Set-Location $msbuildRoot
$worktreeParent = Split-Path $msbuildRoot -Parent
$controlRoot = Join-Path $worktreeParent "msbuild-orchard-control"
$candidateRoot = Join-Path $worktreeParent "msbuild-orchard-candidate"

git worktree add --detach $controlRoot 05f089128b4b06b2c6a489a0602c18a53b18de38
git -C $controlRoot cherry-pick 981c4a4b2c6ba3916be2717fc3b71ab03637a6b0
git worktree add --detach $candidateRoot 981c4a4b2c6ba3916be2717fc3b71ab03637a6b0

& (Join-Path $controlRoot "build.cmd") -v quiet
& (Join-Path $candidateRoot "build.cmd") -v quiet
```

Run fresh-process measurements in alternating order and use the same Orchard checkout,
stage, sharing policy, SDK, environment, and result schema in both arms. Check semantic
manifests, repository checksums, and graph topology before interpreting timing.

When benchmarking copied deployments, pass `--inProcess`. BenchmarkDotNet's default
generated child project references the live benchmark project and can rebuild both copied
arms against one product source. This warning does not apply when each worktree runs its
own source project normally.

## Evidence

The repository contains a compact, reviewable evidence bundle and its checksums in
[OrchardCore-evaluation-globbing-evidence](./OrchardCore-evaluation-globbing-evidence/README.md).
It includes the complete initial archive, final mechanism reports, raw compact paired
measurements, topology hashes, rejected-boundary measurements, and Touki comparison
tables. Large semantic manifests, copied deployments, and traces are indexed but omitted
from Git.

The next credible globbing target is the shallow Legacy corpus, not broader snapshot use:

- 1,881 Legacy selections used `CacheBackedWithoutApplicableExcludes`;
- 1,877 were rooted, spanning 476 unique filespecs and 241 directories;
- 938 SDK selections used only three filespecs;
- 938 project `obj` selections used 470 unique filespecs; and
- these calls cannot use the paired snapshot because they do not match descendants.

Candidate directions are a filename-only shallow specialization, safe sharing of rooted
no-exclude results while retaining project partitioning for relative excludes, and direct
population of final storage to remove intermediate lists and copies. Re-run mechanism,
semantic, and product gates for each one-variable candidate. Do not restore the rejected
file-only (`C2f`) or recursive-no-exclude (`C2g`) snapshot routes.

## Publish the branch

No remote branch existed when this handoff was assembled. From the clean investigation
worktree, publish it with:

```powershell
git push -u origin perf/orchardcore-globbing
```