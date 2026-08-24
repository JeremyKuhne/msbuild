# Filtrace post-mortem for the Orchard Core globbing investigation

## Scope

This document reviews how Filtrace was actually used during the MSBuild Orchard Core
globbing investigation. It covers both the Orchard Core profiling work and the related
Touki comparison work recorded in the same investigation session.

The sections labeled **Observed** describe commands, outputs, or retained artifacts from
that session. The sections labeled **Recommended** describe changes to a future workflow;
they are not claims about work completed in this investigation.

## Evidence standard

The following sources have different evidentiary weight:

1. The investigation session transcript records commands that ran and their
   contemporaneous output. It is the source for the command inventory and failed capture
   attempts below. The transcript is machine-local and is not committed with this branch.
2. The [investigation report](./OrchardCore-evaluation-globbing-performance.md) records the
   conclusions drawn from the traces and the cautions applied to those conclusions.
3. The [evidence index](./OrchardCore-evaluation-globbing-evidence/README.md) and
   [external file inventory](./OrchardCore-evaluation-globbing-evidence/external-evidence-files.csv)
   identify the retained traces by path, size, and SHA-256.
4. The [Filtrace skill](../../../.agents/skills/filtrace/SKILL.md) and
   [capture helper](../../../.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1)
   describe the intended repeatable workflow. They do not prove that a step occurred.

This distinction matters here. The committed guidance includes ETW, sidecar, and manifest
workflows, but the retained Orchard profile contains only EventPipe traces and measurement
JSON. No retained artifact or transcript command establishes that ETW/thread-time,
`gcstats`, source-line analysis, or helper-generated Filtrace sidecars were used.

## Observed usage

The transcript records 65 Filtrace invocations before this post-mortem began:

| Command | Invocations |
| --- | ---: |
| `callers` | 34 |
| `cpu` | 17 |
| `info` | 7 |
| `alloc` | 4 |
| `diff` | 1 |
| `events` | 1 |
| `tree` | 1 |

The tool was pinned at commit `0d121156fd9eb66506c81601ed458716587542ca`.
Three Orchard post-globbing traces were retained and indexed externally:

- `t0-default-graph-cpu.nettrace`
- `t0-degree-one-graph-cpu.nettrace`
- `t0-default-graph-alloc.nettrace`

The retained directory also contains generated `.etlx` caches and the benchmark's
measurement JSON files. It does not contain the Filtrace command output, capture logs,
capture manifests, or Filtrace sidecars used during the session.

### Trace quality gate

**Observed:** The first CPU trace contained 685,645 samples, but `filtrace info` reported
0% managed method-name resolution. The trace was not used to justify a product change.
After recapture with runtime providers and CLR rundown, `filtrace info` reported 36,056
samples with 100% managed method-name resolution.

This was the clearest success in the workflow. The quality gate converted an apparently
large trace from potential evidence into an explicit capture failure before its rankings
could influence implementation work.

### CPU navigation and scoping

**Observed:** CPU analysis proceeded from `cpu` rankings to repeated `callers` queries and
root-scoped views. This was useful in two ways:

- It distinguished whole-process activity from evaluator work.
- It tested whether an apparent hotspot was reached through the operation under
  investigation rather than merely appearing somewhere in the process.

The default-parallel graph trace was dominated by worker and waiting stacks, which made a
whole-process ranking poor evidence for evaluation attribution. A degree-one control made
the call structure easier to interpret. The evaluator-scoped degree-one view contained
1,524 CPU samples.

The analysis also corrected an apparent target-materialization hotspot. Once callers and
scope were inspected, that ranking did not support a product change in the target path.
This was a useful negative result: Filtrace helped reject a candidate rather than merely
produce a list of candidates.

### Allocation analysis

**Observed:** Allocation capture and analysis were kept separate from CPU analysis. The
allocation trace contained 122,577 allocation events. Under the evaluator root, Filtrace
reported 858,064,912 weighted allocation bytes.

The weighted value is an allocation-profile estimate, not an exact byte counter. It was
used for relative attribution, alongside self and inclusive allocation rankings.

The initial whole-process view included topology materialization performed after the
evaluation measurement. Root scoping excluded that work from the evaluator attribution.
Without that correction, benchmark support code would have been reported as part of the
operation being investigated.

### Comparison work

**Observed:** The related Touki work used `info`, benchmark/root-scoped `cpu`, `callers`,
and one `diff` invocation on matched captures. The command record shows that Filtrace was
used as a drill-down tool after benchmark measurements, not as a replacement for those
measurements.

No matched Orchard before/after trace pair is indexed in the retained evidence. The three
retained Orchard traces therefore support hotspot orientation and attribution, but not a
Filtrace-based before/after performance claim.

## What went well

### 1. Bad trace data was detected before interpretation

`info` exposed the 0%-resolved trace immediately and gave a concrete acceptance criterion
for the recapture. This prevented a high sample count from being mistaken for high-quality
evidence.

### 2. The workflow moved from ranking to causality checks

The repeated use of `callers` and evaluator roots was more valuable than the initial flat
rankings. It answered whether a method belonged to the relevant operation and how it was
reached. That process disqualified at least one apparent hotspot.

### 3. A serial control made parallel traces interpretable

The degree-one trace did not represent production parallelism, but it provided a simpler
control for call-path attribution. Keeping both default and degree-one traces avoided
pretending that either view answered every question.

### 4. CPU, elapsed time, and allocation were treated as different signals

The report does not equate CPU sample share with wall-clock cost, and the allocation work
used a separate capture and metric. This avoided using one profiler view to make claims it
could not support.

### 5. Filtrace supported negative decisions

The most defensible outcomes were scope corrections and rejected hypotheses. That is a
productive use of a profiler in an optimization investigation: a candidate was retained
only when independent measurements and semantics also supported it.

## What did not go well

### 1. Capture setup consumed substantial iteration

The first trace had unusable symbol resolution. A subsequent capture command used a
`dotnet-trace` profile name that the installed version did not accept. Another attempt
stalled because a nested `dotnet --version` process inherited startup diagnostics; setting
`MSBuildSDKsPath` avoided that nested SDK-discovery launch.

These were recorder, launch, and workload-environment problems rather than demonstrated
Filtrace analysis defects. Filtrace diagnosed the first problem after capture, but it did
not prevent the capture-time compatibility and process-launch failures.

### 2. The benchmark boundary was not the profiler boundary

The BenchmarkDotNet `WorkloadAction` subtree did not include parallel callback workers.
Conversely, the allocation capture included topology work performed outside the evaluator
measurement. Both cases required manual reasoning about process structure before a root
could be trusted.

Root scoping is ancestry-based. It cannot, by itself, gather sibling worker stacks that are
causally related but not descendants of the selected call-tree node.

### 3. The parallel CPU trace could not explain elapsed time

Idle and waiting worker stacks dominated the default graph profile. EventPipe CPU samples
could show where sampled CPU occurred, but not why wall-clock time elapsed while threads
were waiting. No ETW/thread-time trace was retained, so the investigation could not close
that gap with scheduler evidence.

### 4. The analysis was difficult to reproduce from the portable evidence

The raw traces are indexed, but the exact Filtrace commands and outputs are only in the
session transcript. The transcript records 65 invocations, including 34 caller queries,
which reflects an exploratory manual workflow. There is no committed script that reruns
the decisive analyses and no output snapshot against which a future Filtrace version can
be compared.

The capture helper can write manifests and sidecars, but the retained artifacts do not
establish that it was used for these traces. Retrofitting that helper into the handoff does
not make the historical captures manifest-backed.

### 5. Analyzer breadth exceeded retained evidence breadth

Only one `diff`, one `events`, and one `tree` invocation appear in the transcript. Their
outputs were not retained, so this record does not support a strong assessment of those
commands. There is likewise no observed basis here for evaluating `threadtime`,
`gcstats`, source-line analysis, or ETW handling.

## Responsibility split

| Area | What the evidence supports |
| --- | --- |
| Filtrace analyzer | `info`, CPU ranking, caller traversal, root scoping, and allocation ranking produced useful quality and attribution evidence. No analyzer crash or incorrect parse was established. |
| Trace recorder | Profile-name compatibility and provider/rundown selection caused failed or unusable captures before analysis. |
| Benchmark harness | Nested SDK discovery, parallel worker structure, and post-measurement topology work made the captured process wider than the intended operation. |
| Investigation workflow | Commands and analyzer outputs were not preserved as a replayable analysis bundle, and no Orchard before/after trace pair or ETW control was retained. |

## Recommended next workflow

The following changes are recommendations based on the observed gaps. They were not
completed for the retained traces.

1. Use the capture helper from the first accepted trace and retain its manifest, stdout,
   stderr, environment sidecar, trace hash, and exact analyzer version together.
2. Make `filtrace info` an automatic post-capture gate. Reject traces below an explicit
   managed-name-resolution threshold before running rankings.
3. Record the exact provider list accepted by the installed `dotnet-trace` version rather
   than relying on a profile alias.
4. Capture two CPU controls when parallel callbacks matter: the production parallel shape
   and a degree-one attribution shape. Label the latter as diagnostic, not representative
   throughput data.
5. Add an explicit evaluator phase marker or a validated time window. A call-tree root is
   insufficient when related work runs on sibling workers.
6. Keep topology serialization and other reporting work outside the allocation capture, or
   require an evaluator root and record its included event/weight totals.
7. Add ETW/thread-time capture when sampled CPU cannot account for elapsed time. Report it
   as a separate scheduler/waiting signal.
8. Store a small analysis script that runs the accepted `info`, `cpu`, `callers`, and
   `alloc` queries and writes machine-readable output. Commit compact outputs; index large
   traces by hash.
9. Capture matched baseline and candidate traces under the same workload and scope before
   using `diff` for an Orchard before/after claim.
10. Treat Filtrace rankings as candidate evidence. Require benchmark movement, semantic
    validation, and source inspection before retaining a product change.

## Tool feedback

The observed workflow suggests several Filtrace improvements:

- A machine-checkable `info` quality policy could turn symbol resolution and event
  presence into a failing exit code.
- Root-scoped output could warn that ancestry filtering may omit parallel sibling work.
- A standard analysis-bundle command could persist the trace identity, command line,
  filters, and JSON outputs needed to replay an investigation.
- Capture integration could validate recorder profile/provider compatibility before a long
  workload starts.

These are recommendations derived from this investigation. They are not assertions that
the pinned Filtrace version currently lacks every equivalent mechanism.

## Bottom line

Filtrace was most effective after a valid trace existed. Its quality report, caller
navigation, root scoping, and separate allocation views prevented multiple attribution
mistakes. The weak part of the investigation was the boundary around the analyzer:
capture compatibility, workload/process scoping, and preservation of replayable analysis
artifacts. Future use should keep the same skeptical interpretation while making capture
and analysis reproducible from the outset.