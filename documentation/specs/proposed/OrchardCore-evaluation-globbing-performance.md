# Orchard Core evaluation and globbing performance investigation

## Outcome

The investigation branch is based on MSBuild
`05f089128b4b06b2c6a489a0602c18a53b18de38`, with Orchard Core pinned at
`6a28ae14c64aedcf5c9c748d602fed8696f1e153`. The retained product candidate is
committed as `ed7d5756496954cd5399a3340cf663de115db50b`; the evolved benchmark
harness is committed separately as `981c4a4b2c6ba3916be2717fc3b71ab03637a6b0`.
No candidate met the declared Orchard Core wall-time product gate. Two
semantics-preserving `FileMatcher` improvements remain on the investigation
branch for upstream review: direct traversal avoids resolving link targets for
ordinary directories, and cache-backed optimized callback traversal can populate
its file and directory cache entries from one typed directory snapshot. These are
reported as focused matcher improvements, not as a statistically resolved Orchard
Core wall-time win.

For a fresh-machine checkout, exact commands, comparison worktrees, and evidence
transfer details, start with the
[investigation handoff](./OrchardCore-evaluation-globbing-handoff.md).

### Touki prototype comparison

The historical Touki replay result, 13.89 ms / 3.24 MB for Touki versus
65.38 ms / 8.20 MB for MSBuild 18.4, did not isolate an implementation-speed
difference. The MSBuild arm replayed the `IFileSystem` query responses captured
from an MSBuild traversal, while the Touki arm walked one complete recorded
directory tree. They returned the same files but did not perform the same
traversal work, so the apparent 4.7x difference is not a transferable speedup.

Short diagnostic runs over the same live tree identified two real differences:

| Stage | MSBuild | Touki | Interpretation |
| --- | ---: | ---: | --- |
| Initial optimized direct traversal | 11.377 ms / 198.30 KB | 7.221 ms / 158.93 KB | MSBuild resolved a link target for every directory. |
| Direct traversal after the reparse-point gate | 6.848 ms / 202.32 KB | 7.661 ms / 166.23 KB | The implementations are in the same timing range; movement in the control methods makes the magnitude directional. |
| Initial default cache-backed cold traversal | 14.225 ms / 1,136.98 KB | 8.689 ms / 173.67 KB | MSBuild separately populated file and directory cache entries. |
| Cache-backed traversal after the typed snapshot | 9.952 ms / 1,038.04 KB | 8.942 ms / 173.67 KB | Sharing one enumeration closes most of the timing gap. MSBuild retains additional allocations for its cache and callback contracts. |

The defensible performance claim comes from comparing the final MSBuild candidate
with its MSBuild baseline, not from the historical cross-implementation replay.
Order-reversed focused measurements show a 29.82-30.82% matcher improvement and
approximately 10% lower allocation. Whole-Orchard timing remains statistically
unresolved because globbing is only 4.5% of aggregate evaluation time; allocation
improves by 0.20-0.54% across the measured product workloads.

### Orchard fast-path coverage

Orchard does not send every wildcard call through the cache-backed optimized callback
driver. The retained sequential trace selected `OptimizedCallback` for 751 of 1,701
calls and Legacy for 950; the default graph trace selected `OptimizedCallback` for
2,205 of 4,216 calls and Legacy for 2,011. Of the graph Legacy calls, 1,881 were
intentionally shallow file-only probes with
`CacheBackedWithoutApplicableExcludes`. The other 130 were recursive no-exclude
globs: 126 selections across 21 Orchard translation-package roots, two `wwwroot`
selections, and two fixture selections. Driver logging occurs before the whole-glob
result-cache lookup, so repeated selection counts are an upper bound on traversal.

Driver selection understates coverage of the work that can benefit. A temporary
aggregate observer measured the two predicates controlling each callback directory
step: `MatchesFilesInDirectory` and `CanMatchDescendants`. Across repeated complete
sequential evaluations, 32,792 of 34,776 visited steps, or 94.29%, needed both files
and descendants and therefore used the paired typed snapshot. The other 1,984 steps
were file-only `Assets.*` and `GulpAssets.*` probes; there were no directory-only or
inactive steps. In two fresh default-parallel graph constructions, 31,504 of 32,434
steps, or 97.13%, used the paired snapshot. All 930 misses were file-only: 912
`Assets.*` or `GulpAssets.*` probes and 18 `Properties` JSON probes. Again, there were
no directory-only or inactive misses.

Broadening the selection from
`matchesFilesInDirectory && canMatchDescendants` to `matchesFilesInDirectory` was
tested with an in-process fixture that asserted `OptimizedCallback` selection. It
allows an early file-only probe to publish the directory cache entry for a later
recursive glob, but it also creates directory paths when no later consumer exists.
With 64 root directories, standalone file-only allocation rose from 7.55 KB to
23.71 KB while time moved from 255.6 to 265.4 microseconds. The Orchard-ordered
file-only/file-only/recursive sequence did not improve: 5,066.0 to 5,097.5
microseconds and 201.8 to 207.23 KB. The candidate was rejected and the two-sided
predicate retained. The 1,881 shallow Legacy probes are a separate file-only
optimization problem, not candidates for this paired snapshot.

The shallow Legacy corpus contains 1,881 selections, 1,877 of them rooted, spanning
476 unique filespecs and 241 unique directories. Two dominant cohorts account for 1,876
selections: 938 SDK selections use only three filespecs, while 938 project `obj`
selections use 470 unique filespecs. These calls are selected as Legacy
because cache-backed traversal has no potentially applicable wildcard exclude. They
have no descendant work, so they cannot benefit from the retained two-sided snapshot.

Three narrower directions remain plausible: specialize the filename-only shallow path,
share rooted no-exclude whole-glob results across project directories while preserving
partitioning for relative excludes, and filter directly into final storage to remove
intermediate lists and copies. Each requires a separate semantic and product gate; none
is established by the retained snapshot result.

The 130 recursive Legacy calls were tested separately because they can consume both
entity types. They comprised only 23 unique filespecs, but the 21 translation trees
contained 2,415 files and the graph profiler attributed 104 ms to 14 translation
glob rows. On a representative cold cache with one language directory and 115 files,
routing recursive no-exclude globs to `OptimizedCallback` improved 481.7 to 377.6
microseconds and reduced allocation from 57.98 to 55.28 KB. The complete Shared
semantic manifests were byte-identical when run from one fixed deployment path, and
the default graph checksum and 30,036,476-character topology payload matched.

The product result did not support retaining the broader route. Eight alternating
default-graph pairs measured a 1.57% mean improvement with a 95% interval from a
2.33% regression to a 5.47% improvement; 6 of 8 pairs improved and allocation fell
0.31%. Degree one measured a 0.63% mean regression with a 95% interval from a 4.09%
improvement to a 5.36% regression; 3 of 8 pairs improved and allocation fell 0.04%.
Both intervals cross zero, the default result misses the 3% and 80% consistency
gates, and the control moves in the opposite direction. The route was rejected.
The retained paired snapshot already covers 97.13% of callback directory steps in
the default graph; neither measured expansion of that boundary produced a product
win.

The principal findings are:

- #14663 is statistically neutral for the original command-line and 238-project
  repository evaluation workloads. The
  [preserved before/after report](./OrchardCore-evaluation-globbing-evidence/initial-filematcher/workspace-reports/OrchardCore-65f75f9-evaluation-report.md)
  is embedded with the investigation.
- Globbing is 4.5% of the aggregate evaluation profile. Sequential and adaptive
  callback scheduling candidates did not produce a stable graph improvement.
- The original unified directory snapshot improved the isolated matcher by
  30.09-36.51% but normalized two real Orchard `None` identities from relative to
  absolute paths. Rebuilding entry paths from the caller's lexical root repairs the
  semantic defect. A contemporaneous medium run measured 11.54 ms for T0 and
  7.983 ms for the repaired candidate, a 30.82% improvement, while allocation fell
  from 1.71 MB to 1.54 MB. A fixed-path reverse-order run, with cache warming
  favoring T0, measured 12.27 ms / 1.79 MB for T0 and 8.611 ms / 1.61 MB for the
  candidate, a 29.82% improvement.
- The repaired candidate's 131,721,550-character full semantic manifest is identical
  to T0 after normalizing only the daily `MSBuildSemanticVersion`. All six
  Items/Full cache-policy workloads and both graph degrees preserve their checksums;
  graph topology is also identical.
- Whole-Orchard wall time remains unresolved. The six sequential evaluation means
  range from 1.26% slower to 2.10% faster and both graph means improve by 0.50-1.05%,
  but every 95% interval crosses zero. Process-wide allocation improves in every
  workload: 0.29-0.54% for evaluation and 0.20-0.39% for graph construction.
- Serializing concurrent first misses was rejected. It made the isolated cold path
  4.40% slower with 14.60% more allocation; ten alternating Shared-policy pairs
  measured a 1.38% slowdown `[-12.41%, 9.64%]` and 0.85% more allocation. The
  retained implementation permits duplicate complete snapshots under stable I/O,
  matching `EvaluationContext`'s requirement that callers discard the context when
  I/O changes.
- The corrected CPU trace has 36,056 samples and 100% managed method-name
  resolution. It shows only 95 samples under `ReadTargetElement` and 105 under
  `AddBeforeAndAfterTargetMappings`; the profiler's 27.1% exclusive Targets bucket
  is instrumentation overhead between child frames, not target materialization CPU.
- Two non-globbing candidates passed a focused allocation gate. Direct function-
  argument arrays reduced representative two-argument expansion from 480 B to 408 B,
  and pooled condition state reduced condition evaluation from 176 B to 96 B.
  Neither produced a statistically resolved default-graph speedup: C6 improved 1.20%
  with a `[-3.90%, 6.31%]` interval, and C10 improved 0.31% with a
  `[-2.82%, 3.43%]` interval.
- Copied BenchmarkDotNet deployments must use `--inProcess`. Its default generated
  child project references the live benchmark project and rebuilds product references,
  causing nominal baseline and candidate folders to execute the same live product
  source. Corrected in-process results supersede the original C2/C3 and C4-C7
  out-of-process mechanism rows.

## Purpose

This investigation seeks measurable improvements to MSBuild evaluation on a pinned
Orchard Core checkout. It starts with globbing because item evaluation repeatedly
walks project trees through `FileMatcher`, but it will stop or move to another
evaluation subsystem if profiling shows that globbing lacks enough end-to-end
headroom.

The work is deliberately split into mechanism, product, and attribution evidence:

- **Mechanism:** Does a focused `FileMatcher` or project-evaluation operation get
  faster or allocate less?
- **Product:** Does evaluating the real Orchard Core solution get faster under the
  cache policies used by MSBuild hosts?
- **Attribution:** Did the intended globbing frame or phase shrink?

A mechanism win is not sufficient without a product win. A product movement is not
attributed to a candidate unless the targeted frame or phase also moves.

## Fixed inputs and preserved evidence

| Input | Pinned value |
| --- | --- |
| Historical #14663 parent | `288f4a653261b160f15fe079c39598e7b26d62b9` |
| Historical #14663 target (`T0`) | `65f75f9249a01a3ec8e5220f5173eba3b5db7bc5` |
| Investigation base and new A/B control | `05f089128b4b06b2c6a489a0602c18a53b18de38` |
| Retained FileMatcher candidate | `ed7d5756496954cd5399a3340cf663de115db50b` |
| Evolved benchmark harness | `981c4a4b2c6ba3916be2717fc3b71ab03637a6b0` |
| Orchard Core | `6a28ae14c64aedcf5c9c748d602fed8696f1e153` |
| Orchard Core solution membership | 238 projects from `OrchardCore.slnx` |
| SDK | `11.0.100-preview.7.26360.111` from this repository's `.dotnet` |
| Primary runtime | .NET 11 x64 on Windows |
| Development branch | `perf/orchardcore-globbing`, forked from `05f089128b` |

The complete small initial archive and compact final evidence are committed under
[OrchardCore-evaluation-globbing-evidence](./OrchardCore-evaluation-globbing-evidence/README.md).
The original small archive's file contents were copied from this historical path:

```text
N:\repos\msbuild-perf-reports\2026-08-20-orchardcore-filematcher
```

Its `SHA256SUMS.csv` records the 23 report/result files. The `benchmark-harness`
subdirectory separately records the dirty repository-scale harness as a base commit,
binary full-index patch, complete four-file source snapshot, SHA-256 manifest, and
restore procedure. A clean detached restore at the recorded base reproduced all four
file hashes and byte counts. Keep that archive immutable. New runs must use unique
run directories and must not overwrite either the archive or another run.

The investigation uses pinned copies of these skills:

| Skill | Source commit |
| --- | --- |
| `filtrace` | `JeremyKuhne/filtrace@0d121156fd9eb66506c81601ed458716587542ca` |
| `performance-testing` | `JeremyKuhne/agent-skills@9fc537ec30b2ba8185d30e7bdef2222b88c4899c` |
| `framework-jit-optimization` | `JeremyKuhne/agent-skills@9fc537ec30b2ba8185d30e7bdef2222b88c4899c` |
| `il-copy-inspection` | `JeremyKuhne/agent-skills@9fc537ec30b2ba8185d30e7bdef2222b88c4899c` |
| `scratch-buffer-strategy` | `JeremyKuhne/agent-skills@9fc537ec30b2ba8185d30e7bdef2222b88c4899c` |

Each installed `SKILL.md` records the source repository, exact pin, source path,
and source tree SHA. Preserve that install metadata.

## What the existing measurements establish

The current evidence is a baseline, not a performance claim:

- The original command-line benchmark's wildcard visits only 13 files and 3
  directories. It is a useful startup/control case, but it cannot represent large
  SDK default-item trees.
- Across all 238 projects, the tested FileMatcher commit was statistically neutral.
  Item-stage means ranged from 2.6% slower to 4.0% faster, and all confidence
  intervals overlapped. Full evaluation changed by 0.1% to 1.3%.
- A first sequential A/B run falsely suggested a 10-12% improvement. A reversed run
  showed machine and run-order drift. Every retained comparison therefore needs
  paired, alternating process order.
- One same-process-policy matrix observed approximately 9% lower item-stage time and
  2% lower allocation when all evaluation caches were shared instead of only the SDK
  cache. Run-order drift was material elsewhere in the experiment, so treat this as an
  unconfirmed cache-policy signal until an alternating, paired same-binary comparison
  reproduces it. It is not evidence that the FileMatcher commit caused the movement.
- Fresh-process profiling showed about 5 ms of possible first-use regression in
  globbing after the optimized matcher was introduced. The interval for the
  uninstrumented end-to-end delta still crossed zero.

The pinned Orchard Core tree, excluding `.git`, `bin`, `obj`, and `node_modules`,
contains approximately 9,771 files and 2,709 directories. Project-root file counts
are highly skewed:

| Cohort | Files under project root | Representative project |
| --- | ---: | --- |
| Small (p10) | 3 | `OrchardCore.Rules.Core` |
| Median | 17 | `OrchardCore.Recipes.Abstractions` |
| Large (p90) | 80 | `OrchardCore.Demo` |
| Maximum | 709 files, 239 directories | `OrchardCore.Resources` |

The repository also has meaningful non-C# content: 1,610 `.cshtml` files, 569
JavaScript files, 236 CSS files, and several image, JSON, TypeScript, and template
families. Broad `None`, `Content`, `EmbeddedResource`, module asset, and `wwwroot`
globs therefore matter in addition to `Compile`.

## Post-globbing profile results

The corrected default-parallel EventPipe trace contains 36,056 sampled-thread-time
records and resolves 100% of managed method names. The process-wide view is dominated
by worker waits, so candidate selection used a second degree-one trace scoped to the
1,524 samples under `Evaluator.Evaluate`.

| Evaluator self frame | Share |
| --- | ---: |
| `Array.Copy` | 10.43% |
| `ReferencedItemExpressionsEnumerator.set_Current` | 10.10% |
| `ProjectElementSiblingSubTypeCollection.Enumerator..ctor` | 9.25% |
| `FindFirstFileEx` | 4.99% |
| `DateTime.UtcNow` from evaluation logging | 4.66% |
| metadata `ListSelectIterator.MoveNext` | 3.67% |
| `Path.GetFullPathName` | 3.61% |

The default-graph allocation trace records 122,577 allocation events. After excluding
the benchmark's post-measurement topology snapshot, `Evaluator.Evaluate` accounts for
858,064,912 trace-weighted bytes. Its largest inclusive subtrees are lazy item
materialization (38.19%), import traversal (28.25%), item-group evaluation (22.05%),
condition evaluation (12.78%), and property-group evaluation (12.44%). The largest
isolated allocation sites are distributed: no one untried site exceeds 4.81% of the
evaluator scope. C4-C10 test the concrete non-I/O sites with plausible local edits.

The retained traces are under `post-globbing-profile-20260820`:

| Trace | SHA-256 |
| --- | --- |
| `t0-default-graph-cpu.nettrace` | `D1C702A1A412153DDA16FAE7CA17BA17523B6F4FEDACD2F683E38B26D0BBB33C` |
| `t0-degree-one-graph-cpu.nettrace` | `B190BB330E7AF42D415D56F53963522CD8E912391B3D809384C1A9AB8857708A` |
| `t0-default-graph-alloc.nettrace` | `FFC516F18267E75C1FE052FEB36039CE8647B0FE50B04C595421D073C13A7D9A` |

## Initial hypothesis and cheap falsification

The first hypothesis is:

> On Orchard Core's cache-backed evaluation path, per-directory scheduling,
> include/exclude state recomputation, and directory-entry/result materialization
> cost more than the path-matching automaton itself.

The code gives this hypothesis concrete reasons:

- Evaluation constructs `FileMatcher` with a directory-entry cache, selecting the
  optimized callback driver when an applicable wildcard exclude exists.
- The callback driver starts a `Parallel.For` when the root has at least two
  subdirectories. Most Orchard Core project trees are small, so task startup and one
  result list per root child can exceed useful parallel work and can amplify first-use
  costs.
- The callback path requests files and directories separately and stores them under
  separate cache keys. The repaired implementation shares a typed snapshot only for
  optimized callback directory steps that will consume both entity types; legacy,
  fallback, one-sided callback, injected-filesystem, and unsupported Microsoft.IO
  paths retain their original enumeration behavior. It also builds relative path
  strings and recomputes active excludes as it descends.

The cheapest discriminating check is one CPU trace, one allocation trace, and one
untimed traversal-counter run of an isolated representative case under
`ItemsSharedSdkCache` and `ItemsShared`. Scope traces by exact process and operation
time window, not only by BenchmarkDotNet's `WorkloadAction`: callback traversal can
run on thread-pool workers whose stacks are not descendants of that method. Verify
that worker-thread samples are present. The hypothesis is rejected if
`MSBuildPathMatcher` consumes most scoped CPU, if scheduling and cache/materialization
frames are not material, or if globbing's elapsed-time share is too small to reach the
product gate even if eliminated.

For a globbing share $p$ measured by elapsed phase instrumentation, and an expected
phase speedup $s$, estimate the maximum product speedup before coding:

$$
S = \frac{1}{(1-p) + p/s}
$$

Do not substitute sampled CPU share for $p$ when parallel workers or filesystem waits
are involved. If the observed share cannot plausibly deliver a 3% product improvement,
stop the globbing investigation and move to the highest evaluation frame in the same
trace.

## Decision gates

Set these gates before testing candidates. Change them only after a baseline-only
noise study, before looking at any candidate result.

### Correctness gate

- Treat `65f75f9249a01a3ec8e5220f5173eba3b5db7bc5` as the semantic baseline for
  optimization candidates. The parent is an experimental arm for evaluating that
  commit, not the semantic oracle for follow-up implementations.
- Emit an untimed canonical manifest from every binary and compare candidate versus
  semantic baseline byte-for-byte. Record solution project order; properties and
  targets in a declared stable name order; item type, evaluation order, identity,
  effective inherited/custom metadata in ordinal name order, and a fixed list of
  path-sensitive built-in metadata such as `FullPath`, `RelativeDir`, and
  `RecursiveDir`. Replace only predeclared Orchard, SDK/toolset, MSBuild deployment,
  NuGet, and temporary roots with stable tokens; preserve all remaining characters
  exactly. First require byte stability from two fresh processes running the same
  binary. Comparing cache policies only within one binary is insufficient because a
  common regression would pass.
- Exact FileMatcher results must match for the real glob corpus, including action,
  excluded filespec, failure text, and returned order where the public caller observes
  order.
- The optimized and legacy test corpora must pass on Windows and Linux.
- Case folding, slash direction, wildcard edge cases, inaccessible paths, symlink
  handling, literal excludes, invalid excludes, and cache result isolation must not
  change.
- In fresh processes, verify three explicit Change Wave modes: Wave 18.11 enabled
  matches the target baseline; disabling the wave matches legacy behavior; and the
  legacy culture-sensitive glob escape hatch retains its documented behavior.

Any semantic difference rejects the candidate before timing.

### Mechanism gate

A candidate proceeds from a short benchmark only if it does at least one of the
following in an affected, representative scenario without regressing the others:

- reduces mean time by at least 10%; or
- reduces allocation by at least 10% while time remains neutral; or
- removes a first-use cost of at least 2 ms from a small or median project.

When comparing copied benchmark deployments, use BenchmarkDotNet's in-process
toolchain. The default out-of-process toolchain generates a project that references
the live benchmark project, rebuilds its project references, and can therefore run
the same live product source for both copied arms. Verify that the benchmark harness
assembly is byte-identical and that only the intended product assembly differs before
each run. The original out-of-process C2 and C3 matcher rows were invalid for this
reason and are superseded by the corrected in-process rows below.

### Product gate

The production-host endpoint is parallel `ProjectGraph` construction using its
default `EvaluationContext.SharingPolicy.Shared`. Run both the default logical-core
degree of parallelism and degree one as a control. The sequential 238-project matrix
remains useful for phase and cache-policy isolation, but `SharedSDKCache` has no
identified production caller in this tree and is not by itself a product result.

Retain a candidate only when the production-host endpoint:

- improves by at least 3% and 15 ms;
- the paired two-sided 95% confidence interval for improvement excludes zero;
- at least 80% of the predeclared alternating process pairs improve;
- graph nodes, edges, evaluated outputs, and failures exactly match the semantic
  baseline; and
- its one-sided 95% upper confidence bound for process-wide allocation regression is
  no more than 1%.

The sequential item/full matrix is a non-inferiority guardrail. For each policy, the
one-sided 95% upper confidence bound for a timing regression must be below both 1%
and 5 ms, and the corresponding allocation bound must be below 1%. A candidate that
only improves a diagnostic policy is reported as a mechanism or cache-policy result;
it is not retained as an Orchard Core product improvement without a named host.

All parallel allocation gates use the delta from
`GC.GetTotalAllocatedBytes(precise: true)` around the exact operation. BenchmarkDotNet
`MemoryDiagnoser` current-thread bytes do not include `ProjectGraph` or `Parallel.For`
worker allocations and are diagnostic only for these scenarios. Use allocation
traces to attribute a process-wide movement.

## Phase 0: stabilize the harness

1. The archived `benchmark-harness/manifest.json`, patch, and four source snapshots
  have been restored in a disposable detached checkout and every recorded hash and
  byte count matched. Reverify the archive before making another harness edit.
2. Port the repository-scale benchmark from the preserved working tree into this
   branch as a measurement-only change:
   - `OrchardCoreRepositoryEvaluationBenchmark.cs`;
   - the repository-mode argument handling in `Program.cs`;
   - the direct runtime dependencies required by in-process SDK evaluation; and
   - its benchmark README.
3. Keep the harness source byte-identical in every baseline, target, and candidate
   worktree. Record its patch SHA-256 if it cannot yet be committed independently.
4. Add a project-subset input that accepts a checked manifest. This permits tiny,
   median, p90, maximum, and full-solution cohorts without changing benchmark code
   between runs.
5. Add an untimed cross-binary semantic-manifest mode. Keep this separate from global
  setup so baseline and candidate files can be compared directly before a benchmark
  launch. Serialize UTF-8 with fixed newlines and escaping; preserve evaluated item
  order, sort unordered property, target, and metadata collections ordinally, and
  apply a frozen root-token map. Fail setup if a same-binary two-process comparison
  is not byte-identical.
6. Retain the existing property query as a non-item control. Keep first-use and warm
   measurements separate.
7. Resolve and record the benchmark target framework instead of assuming it. On the
   pinned repository it is `net11.0`; focused `MSBuild.Benchmarks` coverage must also
   run on `net472` where supported.
8. Run the dry job for every method and verify the full semantic oracle before any
   timing run.

## Phase 1: inventory actual glob work

Run this phase outside measured iterations. Instrumentation is diagnostic and must be
off in retained timing runs.

### Per-glob inventory

Capture one record per expansion with:

- project path and item element location;
- include and ordered excludes;
- selected driver and fallback reason;
- result-cache hit or miss;
- directory-entry cache hits and misses;
- directories requested, visited, and pruned;
- files inspected, matched, and excluded;
- result count and total returned path characters;
- maximum depth, root child count, and exclude count; and
- whether top-level parallel traversal ran.

Use the existing `MSBUILDLOGEXPANDEDWILDCARDS` and
`MSBUILDENABLEDEBUGTRACING` switches for a first driver/filespec inventory. If that
is insufficient, add a temporary internal observer that accumulates counters without
formatting strings. Do not leave per-file logging in timed code.

Aggregate by normalized glob shape, driver, fallback reason, project cohort, and
cache policy. The output should identify both call count and weighted work; a frequent
three-file glob and a rare 700-file glob are different optimization targets.

### Expected Orchard Core shapes

The inventory should confirm or correct this initial list:

- SDK default `Compile`, `EmbeddedResource`, and broad `None` globs with default
  excludes;
- module `EmbeddedResource Include="**\*"` with module and default excludes;
- web content under `wwwroot`, plus `.cshtml`, `.json`, and `.config` patterns;
- `Watch Include="**\*.cs"` with `Recipes`, `Assets`, `node_modules`, `obj`, and
  `bin` exclusions;
- `App_Data` and `Localization` include/remove patterns;
- template content globs; and
- the test project's cross-tree `wwwroot\**\*` include.

Store a compact JSON manifest containing relative paths, glob shapes, counts, and
expected result hashes. Do not store package caches or generated `bin`/`obj` trees.

## Phase 2: build a bounded workload ladder

Use four layers. Every layer must consume the same pinned Orchard Core snapshot or a
manifest reconstructed from it.

### Layer A: matcher mechanism

Apply the benchmark classes introduced by the target commit and add only scenarios
seen in the inventory. Independently vary:

- width: root child count;
- depth;
- files per directory;
- match density;
- number and overlap of excludes;
- direct versus callback driver; and
- cold directory cache, warm directory cache, and warm final-result cache.

Do not combine dimensions into one opaque "large" fixture. A fixture must make the
assertion capable of failing, and every benchmark setup must compare exact legacy and
optimized results before measurement.

### Layer B: representative project evaluation

Evaluate these cohorts through the item and lazy-item passes:

1. p10 small project;
2. median project;
3. p90 project;
4. `OrchardCore.Resources`, the largest observed tree; and
5. a stratified set containing one project from each decile.

Cross each cohort with `Isolated`, `SharedSDKCache`, and `Shared`. Record time,
process-wide allocated bytes, Gen0/Gen1/Gen2, output hash, and glob counters from a
separate diagnostic run.

### Layer C: product evaluation

Evaluate all 238 solution projects once per operation with a fresh
`ProjectCollection` and root `EvaluationContext`. Keep `Items` as the primary endpoint
and `Full` as the guardrail. This sequential matrix isolates evaluation stages and
cache policies; it is diagnostic rather than a production-host simulation. Do not
execute targets or multiply target-framework inner builds in this layer.

### Layer D: production graph construction

Construct the Orchard Core project graph using the normal `ProjectGraph` factory,
which creates a shared evaluation context and evaluates graph work concurrently.
For the primary endpoint, pass and dispose a fresh `ProjectCollection` per operation;
do not use `ProjectCollection.GlobalProjectCollection`. This prevents benchmark
warmups from silently changing the project-root-cache state. Measure the default
logical-core degree of parallelism and degree one. Record graph node and edge
identities, inner-build/global-property identity, construction metrics, process-wide
allocation, and wall clock. If a named production host reuses a collection, add that
fixed lifetime as a separate warm-host endpoint rather than mixing states. This layer
exposes nested concurrency or oversubscription that the sequential repository loop
cannot show.

## Phase 3: establish attributable baselines

### Commit and compatibility arms

Set environment variables before process start because `ChangeWaves` and `Traits`
cache them. Use these explicit arms:

| Arm | Source | Wave/culture mode | Purpose |
| --- | --- | --- | --- |
| `P0` | Parent `288f4a6532` | `MSBUILDDISABLEFEATURESFROMVERSION=18.11`; culture escape unset | Historical parent under legacy semantics |
| `TL` | Target `65f75f9249` | `MSBUILDDISABLEFEATURESFROMVERSION=18.11`; culture escape unset | Target binary's legacy fallback |
| `T0` | Target `65f75f9249` | Wave enabled; culture escape unset | Semantic and performance baseline for candidates |
| `C` | Candidate based on `65f75f9249` | Wave enabled; culture escape unset | Candidate under test |

Gate every candidate by paired `C` versus `T0`, never by `C` versus the parent.
Use `P0` versus `T0` only to reproduce the original change and `P0` versus `TL` to
detect overhead while the target's new path is disabled. Run the optimized traversal
with `MSBUILDUSELEGACYCULTURESENSITIVEFILEGLOBS=1` as a separate correctness arm,
not as a performance baseline.

### Timing and allocation

1. Before any candidate run, perform a same-binary A/A study for every primary
  endpoint using independent `T0` process launches. Use it to choose and then freeze
  the launch count, minimum detectable effect, outlier policy, and confidence
  procedure. Do not tune these after observing a candidate.
2. Build Release binaries independently for every applicable arm and deploy each to
  the same canonical benchmark path before launch.
3. Run at least five alternating independent process pairs. Increase the predeclared
  count if the A/A study shows that five pairs cannot resolve the product gate. Use a
  balanced order such as
  `A-B`, `B-A`, `B-A`, `A-B`, `A-B`, where `A` and `B` are the declared comparison
  arms.
4. Use the same restored Orchard Core tree, SDK, NuGet cache, power plan, environment,
   and benchmark source for every arm.
5. Keep full BenchmarkDotNet JSON, CSV, Markdown, and console logs. Analyze paired
   process means; do not pool iteration rows as independent observations.
6. Measure process-wide allocated-byte deltas around each exact operation, in addition
  to GC counts and any BenchmarkDotNet diagnostics. Report means, standard
  deviations, confidence intervals, pairwise deltas, allocations, and GC counts.
  Benefit requires the predeclared effect and a two-sided paired interval excluding
  zero. Guardrails use the one-sided non-inferiority bounds stated above. Report an
  unstable result as inconclusive after one unchanged rerun.

Start with a dry run, then a short screening run. Confirmation uses at least five
warmups and ten measured iterations per launch unless the baseline noise study shows
that more launches and fewer iterations better capture process-level variation.

### Evaluation profiler

Profile the exact benchmark operation rather than a separate command-line evaluation.
For an untimed diagnostic run, register a `ProfilerLogger` with the same
`ProjectCollection`, set `ProjectOptions.LoadSettings` to
`ProjectLoadSettings.ProfileEvaluation`, and preserve the same cohort, evaluation
stage, global properties, and `EvaluationContext` policy. Retain the `Globbing`, item
pass, lazy-item pass, and total rows. Use these instrumented integer-millisecond
reports for attribution and project ranking, not for throughput claims.

### Filtrace

Capture profiles into unique `BenchmarkDotNet.Artifacts/filtrace-runs/<run-id>`
directories. Record the exact process, benchmark, parameters, symbols, source commit,
and operation count in each manifest.

For EventPipe captures:

1. Run `filtrace info` and require usable managed symbol resolution.
2. Capture one benchmark case per trace. Use exact process scope plus BenchmarkDotNet
  start/stop events to select an operation time window; add a cross-thread activity
  marker if the window is ambiguous. Do not use `--benchmark` as the sole scope for
  callback traversal, because `Parallel.For` worker stacks may sit outside the
  `WorkloadAction` subtree.
3. Rank allocation volume separately, then inspect `gcstats`.
4. Drill from `GetFilesOptimizedImplementation`, `EnumerateDirectory`,
   `MSBuildPathMatcher`, `Parallel.For`, cache lookup, and path construction frames
   into callers and source lines.
5. Use `filtrace diff` only on identically scoped before/after captures.

Take an ETW thread-time capture for Candidate 1, and for any other candidate when
elapsed time materially exceeds sampled CPU. It will distinguish filesystem waits,
thread-pool scheduling, and CPU work across worker threads. Use exact process IDs and
the operation window from the capture manifest; a machine-wide `dotnet` name is not a
safe scope. Native-symbol classification is useful only if runtime, GC, or memory-copy
frames are material.

## Phase 4: screen at most three globbing candidates

Change one material variable at a time. Each candidate gets one simple repair; a
redesign consumes another candidate slot.

### Candidate 1: adaptive callback parallelism

**Hypothesis:** Starting `Parallel.For` for two or more root directories costs more
than it saves on Orchard Core's predominantly small project roots and contributes to
the observed first-use signal.

Screen these as separate implementations:

1. always-sequential callback traversal; then, only if it wins small/median cases but
   loses the maximum case,
2. a threshold based on measured root fan-out or estimated work.

Measure first-use, p10, median, p90, maximum, and full-solution item evaluation. Check
thread-time, per-thread CPU, and thread-pool evidence before introducing a heuristic.
Also run default and degree-one `ProjectGraph` construction to expose nested
parallelism. Do not tune a threshold to one winning run.

### Candidate 2: one directory-entry snapshot per directory

**Hypothesis:** Separate file and directory callback requests cause duplicate
enumeration, cache lookup, lists, and filtering for each visited directory.

Prototype a typed top-level entry snapshot that can serve both file matching and
recursion without extra filesystem stats. Preserve the `IFileSystem` abstraction and
cache lifetime. Reject this candidate if instrumentation shows that the two requests
are already served without material duplicate work or if the added representation
increases retained memory enough to fail the allocation gate.

### Candidate 3: propagate matcher state down the traversal

**Hypothesis:** Recomputing relative paths and active exclude applicability against
every exclude at every directory is visible in CPU or allocation profiles for broad
asset globs.

Carry only the include/exclude state needed by a child instead of repeatedly calling
`Path.GetFileName`, `Path.Combine`, `TryGetRelativeDirectory`, and every exclude
matcher. Preserve lexical path semantics and do not retain spans across recursion.
Use `il-copy-inspection` only if the state is a nontrivial struct, and inspect JIT
assembly before claiming that an emitted IL copy has runtime cost.

### Profile-directed alternatives

Do not start these unless the first profile points at them:

- reduce final cache-key or `ToArray` result-copy allocation;
- improve `MSBuildPathMatcher` transitions or filename matching;
- change `BufferScope<byte>` handling for active excludes; or
- alter global cache policy.

The scratch-buffer skill is relevant only if active-exclude buffer setup is measured
as material. Never increase stack use speculatively.

## Phase 5: confirm a survivor

Only a candidate that passes the mechanism and product pilot gates receives full
confirmation:

1. Run the complete matcher matrix on `net11.0` and `net472`.
2. Run all representative project cohorts and all three cache policies.
3. Run the 238-project item/full matrix and the two `ProjectGraph` degrees of
  parallelism in alternating process order.
4. Run representative Linux timing and allocation guardrails, plus focused Linux and
  macOS correctness coverage for path comparison, case sensitivity, slash handling,
  direct enumeration, and symlink behavior. If product timing is confirmed only on
  Windows, constrain the performance claim to Windows.
5. Re-capture CPU, allocation, and applicable thread-time profiles and verify that the
  targeted frame or phase
   shrank.
6. Run focused FileMatcher, path-matcher, globbing, evaluation, and Orchard benchmark
   tests, followed by the repository build.
7. Run the three fresh-process Change Wave/culture modes from the correctness gate. A
  semantics-preserving implementation change needs no new wave, but it must not create
  a new warning, error, ordering change, or ungated semantic change.

## Experiment ledger

Maintain this table from the first candidate onward, including rejected work:

| ID | Hypothesis | One-variable edit | Scenario and check | Time | Allocation | Target frame | Decision |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| P0 | Parent, legacy mode | None | Historical 238-project sequential matrix | Items 709.6-782.0 ms; Full 727.1-802.8 ms | Items 257.29-265.33 MB; Full 275.75-281.23 MB | N/A | Historical arm; policy-dependent ranges |
| TL | Target, legacy mode | Apply `65f75f9`; disable Wave 18.11 | Legacy fallback ladder | Not run | Not run | N/A | Omitted after the original parent/target comparison was neutral |
| T0 | Target, enabled mode | Apply `65f75f9`; enable Wave 18.11 | Historical 238-project sequential matrix | Items 681.2-802.2 ms; Full 727.6-804.3 ms | Items 255.43-263.76 MB; Full 275.98-281.61 MB | N/A | Candidate baseline; no statistically resolved change from P0 |
| AA | T0 in both arms | None | Standalone identical-arm product control | Not run | Not run | N/A | Alternating pairs and reversed-order controls were used, but no dedicated A/A estimate was retained |
| C1a | Small-tree parallel overhead | Sequential callback traversal | Default graph: 8 alternating pairs; guardrails: 5 pairs each | +3.60% `[-0.44%, 7.65%]`; guardrails -5.85% to +1.36% | -0.51% to 0.00% | `Parallel.For` subtree | Rejected: confidence includes zero and guardrails do not show a consistent benefit |
| C1b | Small-tree parallel overhead | Adaptive shared-`FileMatcher` callback limiter | Default graph: two unchanged 8-pair screens | +4.42% `[-7.19%, 16.03%]`; rerun +2.94% `[-8.27%, 14.14%]` | -0.17% to -0.11% | `Parallel.For` subtree | Rejected: both allowed screens are unstable and only 5/8 pairs improve |
| D1 | Direct traversal resolves every directory link target | Check `ReparsePoint` before `ResolveLinkTarget` | Sequential same-tree short direct matcher screens | Optimized direct 11.377 -> 6.848 ms; control methods also moved: legacy 8.768 -> 10.224 ms and Touki 7.221 -> 7.661 ms | 198.30 -> 202.32 KB | direct recursive enumeration | Retained: removes the measured regression while preserving recursive-link parity; treat the short-run magnitude as directional |
| C2 | Duplicate entry snapshots | Unified typed directory snapshot | Corrected in-process 10-case matcher screen; fixed-path full semantic manifest | -36.51% to -30.09% | +3.02% to +22.53% | enumeration/cache frames | Rejected: semantic manifest changes two `None` identities and `RelativeDir` values from relative to absolute paths |
| C2r | Duplicate entry snapshots with lexical path preservation | Rebuild typed entry paths from the caller's lexical root; scope sharing to two-sided optimized callback steps | Medium direct-capable matcher in both orders; 238-project semantic manifest; 6 evaluation and 2 graph workloads, 8 pairs each | Matcher +30.82% T0-first and +29.82% candidate-first; evaluation -1.26% to +2.10%; graph +1.05% `[-1.46%, 3.56%]` default and +0.50% `[-1.99%, 3.00%]` degree one | Matcher 1.71 -> 1.54 MB and 1.79 -> 1.61 MB; evaluation -0.29% to -0.54%; graph -0.39%/-0.20% | enumeration/cache frames | Retained as a focused semantics-preserving matcher improvement; exact outputs/topology, but no Orchard wall-time claim because every product interval crosses zero |
| C2f | More callback steps could publish paired snapshots | Route every file-matching callback step through the typed snapshot | In-process asserted-`OptimizedCallback` fixture; standalone file-only and Orchard-ordered file-only/file-only/recursive cases, 64 root directories | File-only 255.6 -> 265.4 microseconds; Orchard-ordered 5,066.0 -> 5,097.5 microseconds | File-only 7.55 -> 23.71 KB; Orchard-ordered 201.8 -> 207.23 KB | one-sided root enumeration/cache population | Rejected: no sequence benefit and eager directory materialization more than triples standalone allocation |
| C2g | Recursive no-exclude cache-backed globs can use paired snapshots | Route only recursive no-exclude cache-backed calls to `OptimizedCallback` | Translation-package-shaped cold matcher; fixed-path full Shared manifest; default and degree-one graph, 8 alternating pairs each | Matcher 481.7 -> 377.6 microseconds; default candidate delta -1.57% `[-5.47%, 2.33%]`, 6/8 improve; degree-one delta +0.63% `[-4.09%, 5.36%]`, 3/8 improve | Matcher 57.98 -> 55.28 KB; graph -0.31%/-0.04% | recursive Legacy traversal | Rejected: exact manifest/checksum/topology and a focused mechanism win, but both graph intervals cross zero and the product gate fails |
| C2l | Coordinate concurrent first misses | Retained per-directory double-checked lock | Isolated short matcher; Shared policy, 10 alternating pairs | Isolated -4.40%; Shared -1.38% `[-12.41%, 9.64%]`, 3/10 pairs improve | Isolated +14.60%; Shared +0.85% | snapshot cache miss | Rejected: stable-I/O duplicate enumeration costs less than retaining one lock entry per directory |
| C3 | Repeated path/exclude state | Propagated 64-bit child-applicability mask | Corrected in-process 10-case matcher screen, 15 iterations | -1.25% to +1.43% | -9.96% to +7.09% | path/matcher frames | Rejected: no case reaches the mechanism gate |
| C4a | Item-expression capture copy | Direct enumerator backing-field assignment | Corrected in-process parser benchmark, 3 shapes, 15 iterations | Simple -4.38%; transform +3.32%; multiple +4.87% | unchanged | `ReferencedItemExpressionsEnumerator.set_Current` | Rejected: mixed result and regressions in representative transform/multiple cases |
| C4b | Item-expression capture round trip | `MoveNext(out capture)` for string expansion | Corrected in-process simple item expansion, 4 parameter cases, 15 iterations | +3.63% to +6.29% | unchanged | `ExpandItemVectorsIntoString` | Rejected: every corrected case regresses |
| C5 | Property-group interface enumeration | Internal concrete property collection for evaluator loops | Corrected in-process 256-group evaluation, 15 iterations | +1.36% | unchanged | subtype-collection enumerator construction | Rejected: mechanism regression |
| C6 | Function-argument list and copy | Fill a pre-sized argument array directly | Corrected in-process 4-shape function benchmark; default graph, 8 alternating pairs | Two-argument mechanism -1.74%; graph +1.20% improvement `[-3.90%, 6.31%]`, 27.74 ms, 4/8 pairs | Two-argument mechanism -15%; graph -0.294% | `ExtractFunctionArguments` / `List.ToArray` | Rejected: passes allocation mechanism gate but fails every product timing-confidence/consistency threshold; semantic manifests and topology match |
| C7 | Immutable metadata array copy | Exact-capacity builder plus `MoveToImmutable` | Corrected in-process 500-item evaluation, 15 iterations | +1.18% | -1.83% (43,086 B/op) | `ImmutableArray.Builder.ToArray` | Rejected: neither mechanism threshold is reached |
| C8 | Metadata projection iterator | Populate the immutable metadata builder directly | Corrected in-process 500-item evaluation, 15 iterations | -2.82% | -0.61% (14,372 B/op) | `ListSelectIterator.MoveNext` / `ImmutableDictionaryExtensions.SetItems` | Rejected: neither mechanism threshold is reached |
| C9 | Property subtype-enumerator wrapper | Traverse structurally homogeneous property-group children directly | Corrected in-process 256-group evaluation, 15 iterations | -0.21% | unchanged | subtype-collection enumerator construction | Rejected: mechanism movement is negligible |
| C10 | Per-condition state allocation | Reuse one cleared state object per type and thread | Corrected in-process simple/compound condition benchmark; default graph, 8 alternating pairs | Mechanism +1.17%/-0.44%; graph +0.31% improvement `[-2.82%, 3.43%]`, 7.67 ms, 5/8 pairs | Mechanism -45.45%; graph -2.835% | `ConditionEvaluator.EvaluateConditionCollectingConditionedProperties` | Rejected: passes the allocation mechanism gate but fails every product timing-confidence/consistency threshold; semantics and topology match |
| C10 | Per-condition evaluation-state allocation | Reuse one cleared state object per generic type and thread | Corrected in-process simple/compound condition benchmark; default graph, 8 alternating pairs | Mechanism +1.17%/-0.44%; graph +0.31% improvement `[-2.82%, 3.43%]`, 7.67 ms, 5/8 pairs | Mechanism -45.45%; graph -2.835% | `ConditionEvaluator.EvaluateConditionCollectingConditionedProperties` | Rejected: passes allocation mechanism gate but fails every product timing-confidence/consistency threshold; semantic manifests and topology match |

The branch embeds compact candidate measurements under
[OrchardCore-evaluation-globbing-evidence](./OrchardCore-evaluation-globbing-evidence/README.md).
The heavyweight historical roots retain raw candidate measurements under
`2026-08-20-orchardcore-globbing-implementation/callback-sequential-probe-20260820T220525Z`
and `2026-08-20-orchardcore-globbing-implementation/post-globbing-profile-20260820`.
The final repaired snapshot artifacts are in `final-scoped-snapshot-mechanism-20260821`
and `repaired-scoped-snapshot-product-20260821` beneath the first root. The rejected
coordination experiment is in `coordinated-snapshot-mechanism-20260821` and
`coordinated-snapshot-product-20260821`. The fast-path coverage and recursive
no-exclude experiment are under the historical
`N:\repos\msbuild-perf-reports\2026-08-21-orchard-fast-path-coverage` root. The
evidence bundle indexes the semantic manifests and traces intentionally omitted from Git.

The corrected EventPipe graph trace contains 36,056 sampled-thread-time records with
100% managed method-name resolution. It falsifies the initial target-materialization
hypothesis: only 95 samples are under `ReadTargetElement` and 105 under
`AddBeforeAndAfterTargetMappings`. The evaluation profiler's 27.1% exclusive Targets
bucket includes profiler bookkeeping between tracked child frames and is not a product
CPU attribution. The next concrete leaves were item-expression parsing, property-group
collection enumeration, function-argument array construction, immutable metadata
array finalization, metadata projection, and condition-state allocation; C4a through
C10 record those investigations. A default-graph allocation capture observed 858 MB
inside `Evaluator.Evaluate`; no retained candidate met both the mechanism and product
gates.

Record failures, build errors, and semantic mismatches as failures, not as benchmark
results. A stale binary after a failed build invalidates the run.

## Artifact contract

Use a unique stem such as:

```text
orchardcore-<commit>-<stage>-<policy>-<kind>-<UTC timestamp>
```

Retain:

- exact MSBuild and Orchard Core commits;
- harness patch hash and worktree status;
- exact command and non-secret environment;
- SDK, runtime, JIT, OS, architecture, CPU, power plan, and Defender state;
- full BenchmarkDotNet reports and raw measurements;
- canonical semantic manifests and hashes from each binary;
- inventory/counter manifest;
- trace, filtrace sidecar, symbol directory identity, and analysis commands; and
- the corresponding experiment-ledger row.

For any retained run from a dirty worktree, preserve a binary full-index patch and an
explicitly reviewed archive of relevant untracked inputs, with SHA-256 hashes and a
tested restore procedure. Never archive credentials, package caches, unrelated build
output, or private machine configuration.

## Stop and handoff rules

Stop the globbing branch of the investigation when any of these is true:

- globbing lacks enough measured headroom to reach the product gate;
- all three globbing candidates fail semantic, mechanism, or product gates;
- two controlled reruns remain unstable;
- correctness requires observable behavior changes outside the existing Change Wave;
  or
- the time budget for one candidate exceeds 90 minutes before a product pilot.

At that point, preserve the ledger and profiles and move to the highest inclusive
evaluation frame outside globbing. Likely next domains are property/import evaluation,
item expression expansion, or cache reuse, but the trace - not this plan - chooses the
next target.

## Relevant code and guidance

- [`FileMatcher`](../../../src/Framework/Utilities/FileMatcher.cs)
- [`EvaluationContext`](../../../src/Build/Evaluation/Context/EvaluationContext.cs)
- [`CachingFileSystemWrapper`](../../../src/Framework/FileSystem/CachingFileSystemWrapper.cs)
- [`ProjectGraph`](../../../src/Build/Graph/ProjectGraph.cs)
- [`OrchardCoreEvaluationBenchmark`](../../../src/MSBuild.OrchardCore.Benchmarks/OrchardCoreEvaluationBenchmark.cs)
- [Evaluation profiling](../../evaluation-profiling.md)
- [General performance plan](./General_perf_onepager.md)
- [MSBuild performance skill](../../../.github/skills/optimizing-msbuild-performance/SKILL.md)
- [Vendored performance-testing skill](../../../.agents/skills/performance-testing/SKILL.md)
- [Vendored filtrace skill](../../../.agents/skills/filtrace/SKILL.md)