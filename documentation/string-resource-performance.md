# Indexed string-resource experiment

The internal runtime and incremental generator are adapted from
[Touki](https://github.com/JeremyKuhne/touki/tree/3c58c6a4edc814abbe55d18f607a80982437d87b),
specifically `StringResourceManager`, `SatelliteStringResourceManager`,
`RawResourceReader`, `StreamStringResourceReader`, and the RESX source generator.
The source retains the upstream MIT copyright/license notices.

The extracted implementation reads already-loaded assemblies and runtime-bound
satellites only. It does not add a Touki package, PE metadata/file-mapping stack,
binary-object reader, external-directory probing, or process-global provider.
All public task APIs continue to use `System.Resources.ResourceManager`.

The implementation is **disabled by default** while its validation gates are
being exercised. Set `MSBUILD_USE_INDEXED_STRING_RESOURCES=1` before process
startup to select it for all five built-in catalogs. Do not change the selector
after catalogs initialize. Custom task managers are unaffected.

## Differences from upstream

- Preserve exact RESX values, including whitespace; do not emit fallback literals.
- Cache generated known-key slots per effective UI culture, including implicit
  thread-culture changes. Typed and dynamic calls use the same slots.
- Retain platform primary-before-shared lookup and lazy per-key culture fallback.
  Do not open an unused parent table before checking a child translation.
- Retain platform satellite-bind failure behavior, manifest-name lookup,
  `IgnoreCase`, object/resource-set APIs, resource release, and Framework's
  generated format-method culture behavior.
- Reset table/value caches on release; do not add process-lifetime negative-bind
  state or strong global references to resource assemblies.
- Use platform resource lookup for NativeAOT and neutral-in-satellite fallback.
- Restrict generator integration to explicitly marked neutral catalogs and
  build-only analyzer references. Keep generated localized RESX and satellites
  on the existing localization/package path.

## Static caller migration

Built-in literal resource arguments use generated static accessors rather than
the dynamic string-name compatibility path. Ordinary string properties, such
as `PrimaryResourceCatalog.BuildFinishedSuccess`, and Framework's generated
format methods pass constant catalog indices.

Logging and validation need more than an eagerly loaded string. The generator
also emits `PrimaryResourceCatalog.Resources.BuildFinishedSuccess`-style
properties returning an internal `StringResource` reference: the exact resource
name, a constant slot, and a shared lazy manager provider. Internal helper
overloads accept this reference, preserving successful-validation and
filtered-message fast paths, raw templates and deferred arguments, diagnostic
codes, and resource-name-based help keywords. They select templates with the
effective UI culture and preserve existing formatting-culture behavior.

Slot ownership is checked before indexed access. Primary-before-shared fallback
still probes the complete primary culture chain first. A reference used against
another indexed manager performs raw name lookup instead of reusing the wrong
slot or invoking the generated dynamic dispatcher. Replaced/platform/custom
managers and derived task loggers retain their public string/virtual hooks.
Runtime-discovered keys and separate manifest/signing catalogs remain on their
existing compatibility paths.

Full-form Tasks logging calls name `messageResourceName` explicitly. Otherwise
an applicable public `(string, params object[])` instance overload can hide the
typed extension overload and box the descriptor into message arguments.
Bridge-internal forwarding uses explicit static calls for the same reason.
Regression tests check compiler binding, event metadata, deferred formatting,
filtering, replaced managers, and derived logger overrides.

On .NET Framework, transparent logging proxies use the existing public
string-name hook. Resource references contain process-local manager providers
and must not be marshalled across AppDomains. A proxy regression and the full
GenerateResource fixture cover this path; no serializable descriptor/delegate
or remoting wire-contract change is introduced.

Retain a new bootstrap for the caller-migrated subject. The earlier manager-only
measurements below are historical evidence, not measurements of this static
caller strategy. Compare original versus migrated/platform versus
migrated/indexed using the same commands and independently validated outputs.

## Mechanism benchmarks

From the repository root:

```powershell
.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*StringResource*' -Job short
.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*StringResource*' -LaunchCount 3
```

The lookup benchmark compares the actual platform manager with indexed dynamic
keys and known slots. The sweep benchmark reports one entire catalog sweep per
operation and prints its key count; it is not a per-key nanosecond measurement.
The first-lookup benchmark resets managers for each iteration. It measures
first lookup/table load, not manager construction or fresh-process startup.
Constructor setup and resource release are outside that timed operation.
All benchmarks validate nonempty catalogs and retain returned results.

Run the supported runtime TFMs (`net11.0` and `net472` on Windows).
`netstandard2.0` is a library compatibility build, not a runnable benchmark TFM.

If BenchmarkDotNet reports duplicate project names from an unrelated source
snapshot under the repository's artifacts, do not delete that snapshot.
The existing `--inProcess` toolchain can screen the mechanism without project
generation:

```powershell
.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*StringResourceLookupBenchmark*' -Framework net11.0 `
    -Job short -BenchmarkDotNetArguments @('--inProcess')
```

For confirmation with this toolchain, run the full benchmark in independently
started host processes on each runtime. Do not present in-process repetitions
as independent launches, or run benchmarks concurrently with builds/tests.

## Cold OrchardCore build measurements

Use preserved, complete Release bootstrap snapshots. Build each subject with
`.\build.cmd -c Release -v quiet -nodeReuse 0`, then copy its bootstrap outside
generated build output before building the other subject.

The [cold-build harness](../src/MSBuild.Benchmarks/Resources/Measure-ColdBuild.ps1)
requires explicit subject and control bootstrap executables, an artifact
directory, and a unique run ID:

```powershell
.\src\MSBuild.Benchmarks\Resources\Measure-ColdBuild.ps1 `
    -SubjectDotNet 'D:\perf\baseline\dotnet.exe' `
    -ControlDotNet 'D:\perf\baseline\dotnet.exe' `
    -ArtifactsPath 'D:\perf\runs' -RunId baseline-1 -MaxNodes 8
```

Use `-IndexedResources` for the candidate implementation. Every run inventories
evaluated output directories, rejects paths outside the profiling checkout and
reparse points, deletes only those explicit `bin`/`obj` directories, and restores
outside measurement. It then builds the full Release solution without restore,
build servers, node reuse, or terminal logging. `-Verbosity` selects the classic
console logger level; `-BinaryLog` enables the binary logger as an explicit
scenario variable.

OS/device file caches are not flushed. The experiment is process/compilation
cold, not machine cold. Run only against the dedicated profiling checkout when
no other work is using it. The harness never shuts down unrelated servers.
Root process counters are labeled as root-only; ETW supplies process-tree CPU
attribution.

Add `-CollectEtw -Filtrace <absolute-pinned-filtrace.exe>` from an elevated
Windows shell to capture one independently reset build. Use
[filtrace](https://github.com/JeremyKuhne/filtrace) to orient, rank, drill and
compare with exact capture PIDs and descendant scope. Check subject exit codes,
event loss, sample-interval provenance, and matching PDBs before interpreting
CPU time. Preserve traced and uninstrumented runs separately.

The harness retains each command, identities, output hashes and timing/capture
results. Compare nonempty equivalent output populations between arms.
Binlog-on uninstrumented timing is authoritative for the corresponding
binlog-on scenario. Do not compare it with a binlog-off scenario without labeling
the intentional logging difference.
Do not redirect MSBuild console output to files.

[Compare-ColdBuild.ps1](../src/MSBuild.Benchmarks/Resources/Compare-ColdBuild.ps1)
requires paired, valid uninstrumented run directories, equal input/protocol
identities, and exact assembly output hashes. It rejects failed work and changed
outputs before computing paired median ratios and a seeded percentile bootstrap.
Fewer than ten pairs are explicitly labeled pilot-only. If an A/A run already
has differing hashes, fix the independent semantic oracle or establish and
document a justified normalization before accepting product comparisons;
matching file counts alone are not sufficient.

The `-PeContent` oracle compares all assembly bytes except module MVID, COFF
timestamp, debug-directory/data and strong-name signature. It retains all IL,
other metadata, resources and native sections. The harness records the oracle
version and fingerprint-tool hash. Validate full nonempty A/A output populations
before accepting comparisons; matching counts alone remain insufficient.

### Deterministic profiling corpus

OrchardCore's `ArgumentsFromInterceptor` generator calls `Guid.NewGuid()` for
each file-local interceptor class name. This caused 100 copied output differences
across three unique assemblies even in baseline A/A builds. A metadata-heap
canonicalization experiment identified matching IL, attributes and resources,
but later rejected two copies of the test assembly: random identifier suffix
sharing changed the string heap length by three bytes and shifted the following
PE data. Do not broaden byte masks to accept that layout drift.

The dedicated profiling checkout has a retained calibration patch: use invariant
fixed-width invocation ordinals instead of random GUIDs for these private class
identifiers. The interception bodies, locations and property values are unchanged.
Eight focused interceptor/named-provider tests passed. Original and patched
generator sources and hashes are retained locally. All authoritative comparisons
must use the same calibrated corpus, verified by `orchardGeneratorHash` in each
run identity, and establish full A/A equivalence again. Do not combine timings
from the earlier unpatched corpus with this matrix or describe the calibrated
checkout as pristine.

### Dedicated logging matrix

The agreed matrix is quiet, normal and diagnostic, each with binlog off/on.
Run three alternating baseline/candidate pairs per configuration (36 clean
solution builds) using
[Measure-LoggingMatrix.ps1](../src/MSBuild.Benchmarks/Resources/Measure-LoggingMatrix.ps1).
Normal here means standalone MSBuild's normal console verbosity, not dotnet
build's default minimal verbosity. Keep terminal logging off and output streams
inherited identically in each arm.

```powershell
.\src\MSBuild.Benchmarks\Resources\Measure-LoggingMatrix.ps1 `
    -BaselineDotNet 'D:\perf\baseline\dotnet.exe' `
    -CandidateDotNet 'D:\perf\candidate\dotnet.exe' `
    -FingerprintTool 'D:\perf\oracle\OutputFingerprint.dll' `
    -ArtifactsPath 'D:\perf\logging-matrix' -MaxNodes 8
```

ETW comparisons use normal verbosity only, with binlog off/on in each arm.
Keep these captures and their conversions/analyses outside authoritative
wall-time measurements.

[Measure-TraceConversion.ps1](../src/MSBuild.Benchmarks/Resources/Measure-TraceConversion.ps1)
records a local conversion/analysis stress case with process memory, system
commit, paging and pagefile telemetry. On a user-confirmed dedicated machine,
memory pressure is recorded rather than used as a heuristic stop condition.
Cache-hit/opening, conversion and subsequent stack analysis are distinct phases;
report which operation actually consumes memory.

For a valid, exact-PID CPU export, use
[Get-ResourceCpuLedger.ps1](../src/MSBuild.Benchmarks/Resources/Get-ResourceCpuLedger.ps1)
with filtrace's speedscope export and scoped `info` JSON. It verifies established
milliseconds and accounts for each resource-pipeline stack once. Its overlapping
inclusive groups must not be added. Missing/inlined/unresolved frames are
reported as coverage limitations, not zero-cost methods.

For a large ETW capture, the optional
[ResourceTrace](../src/MSBuild.Benchmarks/Resources/ResourceTrace/ResourceTrace.csproj)
driver uses the pinned `Filtrace.Core` API to load an exact root PID and its
descendants once, retaining scoped info and the same CPU-union groups without
serializing millions of stacks and reloading them. Build it separately from
wall-time measurements with an explicit local `FiltraceDirectory`:

```powershell
dotnet build src\MSBuild.Benchmarks\Resources\ResourceTrace\ResourceTrace.csproj `
    -c Release -p:FiltraceDirectory='D:\repos\filtrace\src\Filtrace\bin\Release\net10.0'
dotnet artifacts\bin\ResourceTrace\Release\net11.0\ResourceTrace.dll --self-test
```

`Measure-TraceConversion.ps1 -Operation ledger` requires the driver DLL,
an explicit SDK executable and exact PID. It verifies that the driver's
`Filtrace.Core` hash matches the pinned CLI. By default it resolves managed
method names from CLR events without the expensive optional source/PDB pass;
this does not remove managed symbol fidelity. Supply `-Symbols` only for a
source-line question, and never infer source coverage from method-name coverage.

Follow the requested
[performance-testing methodology](https://github.com/JeremyKuhne/agent-skills/tree/93cdec64a03da1b7215156c6ee68d7661cba58ed/skills/performance-testing):
screen correctness and mechanism first, alternate paired cold-build arms, then
confirm survivors with retained timing and ETW evidence. Never add nested
inclusive CPU rows or infer elapsed savings from a parallel build's CPU share.

No speedup is implied by these commands or by the selector. Enablement requires
localization/API/output parity, measured resource CPU/allocation improvement,
and no credible cold-build regression with adequate measurement precision.

## Small quiet-build regression investigation

The three full-solution quiet/binlog-off paired ratios were 1.0841, 1.0913 and
0.8668. Their interval crosses 1.0: the 8.4% median is not proof of a causal
resource regression. The user reprioritized investigation to a small quiet,
binlog-off case; the remaining full-solution capture queue was stopped for that
reason, not memory pressure. Completed captures remain retained.

The representative case is `OrchardCore.Navigation.Core`: 19 source files,
three prebuilt project references and a 50,688-byte assembly. The
[small-build harness](../src/MSBuild.Benchmarks/Resources/Measure-SmallResourceBuild.ps1)
compares the original baseline with the same candidate binary using the selector
off/on. It retains NuGet assets and dependency binaries, resets only the selected
project's compilation outputs, and uses fresh root/compiler processes, one node,
quiet console logging, terminal logger off and no binlog.
Build console streams are inherited. Evaluation's `TargetPath` response is
captured in memory, checked for the expected nonempty value, and reprinted
outside timing; it is never redirected to a text file. Each new artifact
directory retains the exact harness and its hash.

```powershell
.\src\MSBuild.Benchmarks\Resources\Measure-SmallResourceBuild.ps1 `
    -BaselineDotNet 'D:\perf\baseline\dotnet.exe' `
    -CandidateDotNet 'D:\perf\candidate\dotnet.exe' `
    -ArtifactsPath 'D:\perf\small-build' -Rounds 10
# Repeat with a new artifact directory and -Phase Evaluate.
```

Ten rounds, three arms per round, produced byte-identical build outputs.
Median build times were 7.885 s baseline, 7.893 s candidate/platform and
7.872 s candidate/indexed. The paired indexed/platform ratio was 0.9981
(seeded bootstrap interval 0.9931-1.0043): no full-build slowdown reproduced.
Evaluation alone exposed cold overhead: medians 782.7 ms platform versus
791.2 ms indexed; paired ratio 1.0145 (1.0018-1.0199).

Filtrace ETW captures used trace-established 0.25 ms CPU weights and exact PIDs.
A single evaluation's resource-pipeline union was 4 ms platform versus 20 ms
indexed. Six fresh evaluations per arm confirmed lookup-root CPU of 16.75 ms
platform versus 92.75 ms indexed. Native symbols identified 50.75 ms of
JIT/compiler/register-allocation leaves in the indexed root, versus 3.25 ms in
the platform root. Inclusive rows are not added.

CLR JIT-start/method-load events identify the generated dynamic key dispatcher:
the engine's `PrimaryResourceCatalog.GetResourceIndex` has 30,212 IL bytes and
averaged 6.47 ms cold compilation; the CLI's dispatcher has 8,133 IL bytes and
averaged 1.71 ms, each once per fresh process. Neither dispatcher was compiled
with the candidate selector off. Additional table/manager helpers also require
first-use JIT. JIT event intervals are elapsed compilation intervals, not
additive CPU or inferred build-time savings.

The measured issue is cold code generation/initialization, not slow warmed
lookup. These runs exercised the transitional manager replacement with
hardcoded string-name callers, not the intended generated static caller path.
Migrate callers and remeasure before deciding whether dynamic dispatch also
needs a compact or lazily sharded implementation. This historical evidence
does not explain the noisy full-solution 8.4% median by itself.

## Caller-migrated results (2026-10-07)

The retained `candidate-static-1` bootstrap contains the migrated callers, not
the earlier manager-only candidate. It uses SDK
`11.0.100-rc.1.26420.103`; matching portable PDBs are embedded in all five
subject assemblies. Assembly hashes, a binary source patch, untracked sources,
and the three caller audits plus explicit-overload-binding audit are retained.
Indexed resources remain **disabled by default**.

### Small quiet build and evaluation

Ten alternating rounds per phase compared the original baseline with the same
migrated bootstrap using the platform and indexed managers. All 30 clean
compilations produced the same nonempty assembly bytes. All 30 evaluations
returned the same nonempty `TargetPath`.

| Operation | Original | Migrated/platform | Migrated/indexed |
|---|---:|---:|---:|
| Clean selected-project build, median | 7.832 s | 7.739 s | 7.831 s |
| Fresh-process evaluation, median | 796.5 ms | 798.6 ms | 806.3 ms |

The indexed/original build paired-median ratio is **1.0015**, with a seeded
95% percentile-bootstrap interval of **0.9812-1.0153**. The indexed/platform
build ratio is 1.0131 (0.9953-1.0216). Neither establishes a build speedup or a
stable regression at the declared precision.

Evaluation retains a small cold overhead: indexed/platform paired ratio
**1.0040** (1.0012-1.0080), about 0.4%; indexed/original ratio 1.0105
(1.0026-1.0206). These are medians of within-round ratios, not quotients of
the arm medians. Do not directly compare absolute timings with the older
manager-only runs from a different measurement period.

### Clean full OrchardCore solution, quiet without binlog

Three alternating pairs used the calibrated profiling corpus, eight nodes,
fresh root/compiler processes, evaluated compilation-output resets, and restore
outside timing. OS file caches were not flushed. Every pair passed the strict
8,012-assembly PE-content oracle, excluding only the previously documented
identity/debug/signature fields.

| Pair | Original | Migrated/indexed | Candidate/original |
|---|---:|---:|---:|
| 1 | 409.902 s | 405.544 s | 0.9894 |
| 2 | 405.681 s | 407.734 s | 1.0051 |
| 3 | 368.788 s | 434.281 s | 1.1776 |

The paired median is **1.0051** (about 0.5% slower), with a wide pilot interval
of **0.9894-1.1776**. Three pairs, especially the variable third pair, do not
establish a solution-build improvement or a causal resource regression.
This is a new quiet/binlog-off pilot, not a rerun of the old 36-build logging
matrix. No default-enablement claim follows from it.

### Single diagnostic/binlog full-solution comparison

One additional clean Release pair used the same calibrated corpus and eight-node
protocol, with **diagnostic console verbosity and binlog enabled** in both arms.
Restore remained outside timing; console streams were inherited, terminal
logging and build/compiler/node reuse were disabled, and no ETW ran concurrently.
The original baseline ran first, followed by the caller-migrated indexed subject.

| Subject | Wall time | Binlog bytes |
|---|---:|---:|
| Original baseline | 728.956 s | 25,108,984 |
| Migrated/indexed | 739.519 s | 25,377,507 |

The migrated subject took **10.563 s longer (1.45%)** in this single comparison.
Both builds succeeded, produced nonempty binlogs, and passed the strict
8,012-assembly PE-content output-equivalence gate. A single pair is a point
comparison only; it does not establish a performance trend or useful confidence
interval. Evidence is retained in `single-diagnostic-binlog-20261007` under the
caller-migration artifact directory.

### Verify the intended static path

Separate filtrace captures contain six successful fresh evaluations per manager
selection. Exact-PID CLR JIT-start/load analysis reports no lost ETW events and
**zero `GetResourceIndex` JIT starts or completions** in either arm. Generated
catalog accessors and `StringResource` methods do compile, confirming the new
reference path is exercised. Duplicate starts for the same method identity are
recorded separately; dispatcher absence does not depend on completion matching.
The earlier giant dynamic dispatchers are no longer compiled in this case.

The CPU ledger now recognizes generated catalog accessors and resource
references as well as the existing wrappers. One exact first-process ledger
per arm, with trace-established 0.25 ms weights, reports 5.25 ms platform versus
10.25 ms indexed resource-pipeline union. These single-process samples are a
cold-cost sanity check, not a six-process aggregate or a prediction of elapsed
savings. JIT intervals, sampled CPU and authoritative wall time remain separate.

### Validation and retained evidence

- Complete Release build passed: zero warnings/errors, 30.60 s.
- Generator tests: 12 passed. Typed logging/empty-diagnostic checks with the
  selector off: 35 passed, zero failures/skips across both runtimes.
- The indexed full suite passed the migration-facing Framework, Tasks,
  Utilities, CLI, BuildCheck and object-model projects on both runtimes, and
  the .NET engine. It is **not reported as a successful full-suite command**:
  one Framework binlog round-trip case failed and the unchanged .NET
  coordinator host hung on a synchronous pipe read.
- The hung host alone was terminated with explicit user approval, after
  retaining its stack and log; no unrelated coordinator code was changed.
  The isolated coordinator rerun passed 1/1. The complete binlog round-trip
  matrix rerun passed 10/10 per runtime, zero skips.
- Retained bootstrap help and the repository Dependency sample build passed.
  The resource CPU ledger's static-accessor/union self-test passed.

Evidence is retained under
`D:\repos\plans\artifacts\msbuild-string-resources\20261005-touki\static-callers-20261006`:
`small-build`, `small-evaluate`, `full-quiet-off`, `evaluation-etw-complete`,
and `validation`. The incomplete initial capture/conversion attempt is kept
separately and is not used for the JIT or CPU results above.
