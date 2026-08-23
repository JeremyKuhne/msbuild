# OrchardCore evaluation impact of FileMatcher optimization

Date: 2026-08-20

## Executive summary

Commit `65f75f9249a01a3ec8e5220f5173eba3b5db7bc5` is neutral in both the original
single-project command-line benchmark and a new repository-scale benchmark on this
machine.

- The repository benchmark evaluates all 238 projects in `OrchardCore.slnx` per
  operation, under item-only and full evaluation with three cache-sharing policies.
- In the final adjacent item-stage pair, means ranged from 2.6% slower to 4.0% faster.
  Every before/after confidence interval overlapped.
- Full-evaluation means changed by +0.1% to +1.3%, also within noise.
- Item-stage allocations fell by 0.6% to 1.4%; full-evaluation allocations changed by
  less than 0.3%.

- Warm `GetItems`: 17.902 ms before, 17.806 ms after (`-0.096 ms`, `-0.54%`).
- Warm `GetProperty` control: 16.373 ms before, 16.388 ms after (`+0.09%`).
- Warm `GetItems` allocation: 7,177,365 B before, 7,179,287 B after (`+0.027%`).
- The warm timing delta is much smaller than independent-run variation. It is not
  evidence of either a regression or an improvement.
- Fresh-process measurements show a possible first-use item-stage cost of about
  7 ms after the commit, but its 95% interval crosses zero. Instrumented profiles
  place about 5 ms of that signal in first-use glob evaluation.

The benchmark does execute the new cache-backed `OptimizedCallback` driver. However,
its only measured glob traverses a very small project directory: 13 files and 3
directories after restore, with `obj` and `Recipes` excluded and only one C# file.
These results should not be generalized to large SDK default-item trees.

## Repository-scale follow-up

The follow-up benchmark uses the canonical `OrchardCore.slnx` membership from the same
pinned Orchard Core checkout. It evaluates 238 existing projects: 211 product/tooling
projects, 25 test projects, and 2 template projects. Six tracked project files that are
not solution members are intentionally excluded.

One operation evaluates every project sequentially with a fresh `ProjectCollection` and
fresh root `EvaluationContext`. Solution parsing is performed once in global setup. The
matrix crosses two evaluation stages with three cache policies:

| Stage/policy | Meaning |
|---|---|
| `ItemsIsolated` | Item and lazy-item passes; no state shared between projects |
| `ItemsSharedSdkCache` | Item passes; SDK resolution shared, filesystem/glob caches fresh per project |
| `ItemsShared` | Item passes; SDK, filesystem, directory-entry, and glob caches shared across the solution |
| `FullIsolated` | Full evaluation; no state shared between projects |
| `FullSharedSdkCache` | Full evaluation; only SDK resolution shared |
| `FullShared` | Full evaluation; all evaluation caches shared across the solution |

`SharedSdkCache` is the baseline within each stage. The benchmark evaluates each
top-level project once; it does not execute targets or expand target-framework inner
builds.

### Protocol and run-order control

Both exact revisions used identical benchmark source in detached worktrees. Each
higher-sample run used one launch, five warmup iterations, and ten measured iterations;
each iteration represents 238 project evaluations. The same restored Orchard Core tree,
SDK, NuGet cache, and machine were used throughout.

The first sequential item-stage pair appeared to improve isolated and SDK-cache-only
evaluation by 9.8% and 12.1%. A reversed-order parent control then dropped into the same
range as the optimized build, showing that machine/run-order state was material. The
table below therefore uses the final adjacent parent-then-optimized pair rather than the
misleading first sequence.

### Item-stage results

| Cache policy | Before mean | After mean | Delta | Before 99.9% CI | After 99.9% CI |
|---|---:|---:|---:|---:|---:|
| Isolated | 782.0 ms | 802.2 ms | +20.2 ms (+2.6%) | 747.5-816.4 ms | 752.7-851.6 ms |
| Shared SDK cache | 775.5 ms | 752.6 ms | -22.9 ms (-3.0%) | 723.5-827.4 ms | 728.3-777.0 ms |
| Fully shared | 709.6 ms | 681.2 ms | -28.4 ms (-4.0%) | 658.8-760.3 ms | 662.2-700.3 ms |

All intervals overlap. The results support no statistically resolved throughput change;
they bound the real-repository item-stage effect to the low-single-digit range on this
machine.

| Cache policy | Before allocation | After allocation | Delta |
|---|---:|---:|---:|
| Isolated | 265.33 MB | 263.76 MB | -1.57 MB (-0.6%) |
| Shared SDK cache | 263.33 MB | 259.75 MB | -3.58 MB (-1.4%) |
| Fully shared | 257.29 MB | 255.43 MB | -1.86 MB (-0.7%) |

The small allocation reductions are directionally favorable, but the SDK-cache-only
allocation varied between independent parent runs and should not be treated as a precise
effect size.

### Full-evaluation results

| Cache policy | Before mean | After mean | Delta | Before 99.9% CI | After 99.9% CI |
|---|---:|---:|---:|---:|---:|
| Isolated | 802.8 ms | 804.3 ms | +1.5 ms (+0.2%) | 772.5-833.0 ms | 775.0-833.6 ms |
| Shared SDK cache | 792.6 ms | 803.1 ms | +10.5 ms (+1.3%) | 766.7-818.5 ms | 772.5-833.8 ms |
| Fully shared | 727.1 ms | 727.6 ms | +0.5 ms (+0.1%) | 696.7-757.5 ms | 708.0-747.3 ms |

Full-evaluation allocations changed from 281.23 to 281.61 MB (isolated), 279.52 to
280.30 MB (SDK cache only), and 275.75 to 275.98 MB (fully shared). Those changes are
all below 0.3%.

Across both stages, fully sharing evaluation caches reduced the optimized revision's
aggregate time by about 9% relative to SDK-cache-only evaluation and reduced allocation
by about 2%. That cache-policy result is useful for hosts evaluating many related projects,
but it is separate from the before/after effect of the FileMatcher change.

## Compared revisions

| Role | Commit | Subject |
|---|---|---|
| Before | `288f4a653261b160f15fe079c39598e7b26d62b9` | Add Orchard Core command-line evaluation benchmark (#14634) |
| After | `65f75f9249a01a3ec8e5220f5173eba3b5db7bc5` | Optimize FileMatcher wildcard enumeration (#14663) |

The benchmark source is identical at both revisions. The before revision is the
first parent of the optimization commit, so there are no intervening changes.

Local `main` was fetched and fast-forwarded again after measurement because another
commit landed during the run. At report completion it was clean and matched
`upstream/main` at `c9b4d14f91c2de6d46f9e43f31603dc93b8aab4d`.

## Workload and environment

- OrchardCore revision: `6a28ae14c64aedcf5c9c748d602fed8696f1e153`
- Project: `src/OrchardCore.Cms.Web/OrchardCore.Cms.Web.csproj`
- SDK: `11.0.100-preview.7.26360.111`
- Runtime: .NET 11.0.0, x64 RyuJIT, AVX2, concurrent workstation GC
- BenchmarkDotNet: 0.13.12
- OS: Windows 11 Pro for Workstations, build 26200
- CPU: Intel Core i9-14900K, 24 cores / 32 logical processors
- Memory: 127.7 GiB
- Power plan: High performance
- Windows Defender was active; BenchmarkDotNet reported its standard interference warning.

The project was restored once before measurement. Both MSBuild revisions used the
same checkout, SDK, NuGet cache, environment, and power plan.

## Benchmark protocol

The committed `OrchardCoreEvaluationBenchmark` invokes the MSBuild command-line
implementation in process:

- `GetProperty` requests `TargetFrameworks` and stops after evaluation pass 1.
- `GetItems` requests `PackageReference` and evaluates through passes 3 and 3.1.
- Each benchmark invocation performs 100 evaluations.

Three independent BenchmarkDotNet ShortRun invocations were taken for each revision
in `A-B-B-A-A-B` order. Each invocation used 3 warmup and 3 measured iterations.
This produced 9 measured iterations and 900 measured evaluations per method per
revision. Release binaries were built independently in detached worktrees.

The benchmark initially failed for both revisions with `MSB4184` because
`NuGet.Frameworks.dll` did not flow to the benchmark app from Microsoft.Build's
`PrivateAssets="all"` reference. An identical direct `NuGet.Frameworks` package
reference was added only to each temporary benchmark worktree. This mirrors the
existing AOT evaluation harness and made the in-process SDK evaluation runnable.
No product source was changed.

## Warm steady-state results

Times are per evaluation. The three values in each run column are independent
ShortRun means, not individual iterations.

| Method | Before runs (ms) | After runs (ms) | Before mean | After mean | Delta |
|---|---:|---:|---:|---:|---:|
| `GetProperty` | 16.509, 16.309, 16.300 | 16.125, 16.144, 16.895 | 16.373 ms | 16.388 ms | +0.015 ms (+0.09%) |
| `GetItems` | 17.768, 17.786, 18.151 | 17.460, 17.448, 18.509 | 17.902 ms | 17.806 ms | -0.096 ms (-0.54%) |

The before `GetItems` run means span 17.768-18.151 ms; the after means span
17.448-18.509 ms. The 0.096 ms aggregate delta is well inside this variation.

### Managed allocation

| Method | Before | After | Delta |
|---|---:|---:|---:|
| `GetProperty` | 6,344,940 B | 6,347,247 B | +2,307 B (+0.036%) |
| `GetItems` | 7,177,365 B | 7,179,287 B | +1,922 B (+0.027%) |

Using `GetProperty` as a common-overhead control, the incremental warm item-stage
cost is 1.529 ms before and 1.418 ms after (`-0.111 ms`, `-7.3%`). With only three
independent process means and an approximate uncertainty of more than 0.8 ms, that
normalized delta is also unresolved. Incremental allocation changes from 832,425 B
to 832,040 B (`-385 B`, `-0.046%`).

## Fresh-process first-use results

A temporary one-shot mode measured the first unprofiled `MSBuildApp.Execute` call in
12 new processes per query and revision. The order alternated revisions and query
order. These measurements include MSBuild initialization and result formatting, but
exclude process startup before `Main`.

| Query | Before mean | After mean | Delta | Approx. 95% interval for delta |
|---|---:|---:|---:|---:|
| Property control | 186.70 ms | 186.32 ms | -0.38 ms (-0.21%) | +/-3.92 ms |
| Item query | 242.48 ms | 249.08 ms | +6.60 ms (+2.72%) | +/-8.03 ms |
| Item minus property | 55.78 ms | 62.76 ms | +6.98 ms (+12.5%) | +/-8.17 ms |

The direction is consistent with extra first-use work in the optimized matcher, but
the confidence interval includes zero. It should be treated as a follow-up signal,
not a demonstrated regression.

Fresh-process allocation was also effectively neutral:

| Query | Before | After | Delta |
|---|---:|---:|---:|
| Property control | 7,935,608 B | 7,939,584 B | +3,976 B (+0.050%) |
| Item query | 8,926,591 B | 8,932,730 B | +6,139 B (+0.069%) |
| Item minus property | 990,983 B | 993,146 B | +2,163 B (+0.218%) |

## Evaluation-profiler attribution

Three fresh processes per revision ran the item query with `/profileEvaluation`.
The profiler is instrumented and reports integer milliseconds, so these values are
for attribution rather than throughput comparison.

| Scope | Before mean (range) | After mean (range) | Delta |
|---|---:|---:|---:|
| Total evaluation | 157.0 ms (155-159) | 172.0 ms (165-186) | +15.0 ms |
| Properties, pass 1 | 98.0 ms (96-100) | 107.7 ms (99-122) | +9.7 ms |
| Items, pass 3 | 28.7 ms (28-29) | 29.0 ms (28-30) | +0.3 ms |
| Lazy items, pass 3.1 | 18.0 ms (17-20) | 23.7 ms (23-24) | +5.7 ms |
| Globbing | 4.3 ms (4-5) | 9.3 ms (9-10) | +5.0 ms |

The stable 5 ms first-use glob difference is consistent with JIT/setup cost for the
new matcher. The unrelated and noisy properties-pass increase, including a 122 ms
outlier, explains why total-profile deltas must not be treated as benchmark results.

## Actual wildcard workload

The item profile contains one glob:

```text
root: OrchardCore.Cms.Web
pattern: **\*.cs
excludes: Recipes\**; Assets\**; node_modules\**\*; **\*.js.map; obj\**\*; bin\**\*
```

After restore, that root contains 13 files and 3 directories (`obj`, `Properties`,
and `Recipes`). `obj` and `Recipes` are excluded, and `Program.cs` is the only C#
file. Evaluation supplies the directory-entry cache. The include has an empty fixed
root and at least one wildcard exclude overlaps it, so the after revision selects
the cache-backed `OptimizedCallback` driver without fallback.

This explains the result shape:

1. `GetProperty` never reaches item or glob evaluation and is a useful noise control.
2. `GetItems` does reach the optimized driver, but the traversal has almost no work.
3. Property/import evaluation dominates total time.
4. The optimization's reported gains on wider and deeper SDK-style trees are not
   expected to be visible in this particular OrchardCore project directory.

## Correctness and validation

- Both revisions passed the benchmark's dry job after the symmetric dependency fix.
- All 238 solution projects evaluated successfully at both item and full stages.
- The repository benchmark validates ordered project, property, item, and target results
  across isolated, SDK-cache-only, and fully shared evaluation contexts.
- All six profiled item queries produced byte-identical JSON with SHA-256
  `04DBE9CD7B306252BDE17F57F22A87FBA4D3AA8B64C92FD551F588E0974E8BA6`.
- The query returned the expected non-empty `PackageReference` result.
- `build.cmd -v quiet` at the optimized revision succeeded with 0 warnings and 0 errors.
- The follow-up adds only benchmark code, runner plumbing, runtime dependencies required
  by the in-process host, and benchmark documentation; no product behavior is changed.

## Conclusion

The original single-project command-line benchmark remains neutral and is too small to
represent a large glob workload. The repository-scale follow-up exercises every project
in the real Orchard Core solution and likewise finds no statistically resolved timing
regression or improvement. Item-stage means suggest at most a low-single-digit benefit
when caches are shared, while complete evaluation is flat and allocation changes are
small.

The new repository benchmark is the more representative regression guard: it retains
the narrow command-line case as a control while covering 238 real project trees under
fresh and shared evaluation-cache policies.