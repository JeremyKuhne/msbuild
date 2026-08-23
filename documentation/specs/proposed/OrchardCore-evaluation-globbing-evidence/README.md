# Orchard Core investigation evidence

This directory carries the compact evidence needed to review or resume the Orchard Core
evaluation and `FileMatcher` investigation. See the
[handoff](../OrchardCore-evaluation-globbing-handoff.md) for setup and execution commands
and the [report](../OrchardCore-evaluation-globbing-performance.md) for interpretation.

## Contents

| Directory | Contents |
| --- | --- |
| `initial-filematcher` | Content-preserving copy of the complete 30-file, 78,967-byte initial archive, including its original `SHA256SUMS.csv` |
| `retained/mechanism` | Final T0/candidate BenchmarkDotNet reports in both measured orders |
| `retained/product` | Six sequential and two graph workloads, each with 16 alternating measurements |
| `controls/coordinated-first-miss` | Rejected lock/no-lock mechanism reports and the 10-pair Shared-policy control |
| `coverage` | Driver, paired-snapshot, shallow Legacy, and recursive Legacy corpus counts |
| `rejected/file-only-snapshot` | Compact mechanism summary for rejected candidate `C2f` |
| `rejected/recursive-no-excludes` | The two 8-pair graph controls for rejected candidate `C2g` |
| `post-globbing` | Focused and graph evidence for rejected allocation candidates `C6` and `C10` |
| `touki` | Compact live-tree and historical replay BenchmarkDotNet reports |

The sequential product CSVs are copied raw. The graph CSVs are compact projections of
the original per-process JSON: they preserve arm, pair, order, elapsed ticks and
milliseconds, process-wide allocation, checksum, node/edge counts, source filename,
topology length, and SHA-256 of the UTF-8 topology JSON. The repeated 30,036,476-character
topology value is not embedded in every row.

The historical benchmark project is stored as
`MSBuild.OrchardCore.Benchmarks.csproj.snapshot`. Its bytes are unchanged, but the
non-project suffix prevents BenchmarkDotNet from discovering it as a second live project.
Rename it back to `.csproj` only when reconstructing the historical source snapshot
outside this repository.

`SHA256SUMS.csv` hashes every file in this compact directory except itself. Regenerate it
only when intentionally changing the evidence set.

## External evidence

The following historical source roots remain on the original machine. They are optional
for continuing from source, but retain copied deployments, full semantic payloads, and
raw traces for deep audit:

| Archive key | Historical path | Files | Bytes |
| --- | --- | ---: | ---: |
| `implementation` | `N:\repos\msbuild-perf-reports\2026-08-20-orchardcore-globbing-implementation` | 13,774 | 17,677,210,617 |
| `fast-path` | `N:\repos\msbuild-perf-reports\2026-08-21-orchard-fast-path-coverage` | 964 | 2,153,296,723 |
| `touki-oracle` | `N:\repos\msbuild-perf-reports\2026-08-20-touki-msbuild-oracle` | 96 | 46,420,220 |

`external-evidence-files.csv` records size and SHA-256 for 16 authoritative semantic
manifests and raw traces totaling 1,171,662,790 bytes. It intentionally omits copied
binaries, package/build outputs, generated `.etlx` files, and live logs. Those can be
rebuilt from the pinned branch and do not need to move with the investigation.

After copying selected external files, verify them with paths appropriate to the new
machine:

```powershell
$archiveRoots = @{
    "implementation" = "D:\perf\2026-08-20-orchardcore-globbing-implementation"
    "fast-path" = "D:\perf\2026-08-21-orchard-fast-path-coverage"
    "touki-oracle" = "D:\perf\2026-08-20-touki-msbuild-oracle"
}

Import-Csv .\external-evidence-files.csv | ForEach-Object {
    $path = Join-Path $archiveRoots[$_.Archive] $_.Path
    if ((Get-Item $path).Length -ne [long]$_.Bytes) {
        throw "Size mismatch: $path"
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -cne $_.SHA256) {
        throw "SHA-256 mismatch: $path"
    }
}
```