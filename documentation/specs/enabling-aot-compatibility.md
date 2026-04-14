# Plan: Enabling AOT Compatibility in MSBuild

## Current State

MSBuild has **zero AOT/trimming infrastructure** today:
- No `IsAotCompatible`, `IsTrimmable`, `EnableTrimAnalyzer`, or `EnableAotAnalyzer` properties set
- No `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]`, or `[DynamicallyAccessedMembers]` attributes
- No `ILLink.Substitutions.xml`, `ILLink.Suppressions.xml`, or `ILLink.Descriptors.xml` files
- No JSON source generation (`JsonSerializerContext`)
- Heavy use of reflection for plugin loading (tasks, loggers, SDK resolvers)
- `Reflection.Emit` usage in COM interop (`TlbReference.cs`)

**Already AOT-safe:** `MSBuildEventSource` uses only primitive-type `WriteEvent()` calls (manifest-based). `SolutionFile.cs` uses `JsonDocument.Parse()` which is AOT-safe.

## Source Projects In Scope

| Project | Target Frameworks | Notes |
|---------|-------------------|-------|
| `Microsoft.Build.Framework` | net472, net10.0, netstandard2.0 | Public API, NuGet-shipped |
| `Microsoft.Build` | net472, net10.0 | Core engine |
| `Microsoft.Build.Tasks.Core` | net472, net10.0, netstandard2.0 | Built-in tasks, has Reflection.Emit |
| `Microsoft.Build.Utilities.Core` | net472, net10.0, netstandard2.0 | Task authoring utilities |
| `MSBuild` (CLI) | net472, net10.0 | Exe, uses System.Text.Json |
| `Microsoft.NET.StringTools` | net472, net10.0, netstandard2.0, net35 | String interning, no reflection |

**Out of scope:** Test projects, `MSBuildTaskHost` (net35 only), `ThreadSafeTaskAnalyzer` (Roslyn analyzer, netstandard2.0 only), Bootstrap projects.

## Multi-Targeting Constraint

Three of the six projects target `netstandard2.0` and/or `net472`/`net35`, where `System.Diagnostics.CodeAnalysis` AOT attributes don't exist. The MSBuild repo's `netstandard2.0` builds produce **reference-only assemblies** (no runtime output), so AOT annotations on those TFMs are only needed for API surface correctness.

**Strategy:** Condition `IsAotCompatible` on `net10.0` (and future TFMs) only. Use **internal polyfill attributes** for net472/netstandard2.0 so annotations compile on all TFMs but are only enforced by analyzers on net10.0+.

---

## Phased Plan

### Phase 0: Infrastructure Setup

#### 0.1 — Add AOT attribute polyfills for downlevel TFMs

Create `src/Shared/Polyfills/TrimmingAttributes.cs` with internal definitions of:
- `RequiresUnreferencedCodeAttribute`
- `RequiresDynamicCodeAttribute`
- `DynamicallyAccessedMembersAttribute` / `DynamicallyAccessedMemberTypes`
- `UnconditionalSuppressMessageAttribute`

Guard with `#if !NET5_0_OR_GREATER` so they only compile for net472/netstandard2.0/net35. The compiler recognizes these attributes by name regardless of assembly, so the trimmer will pick them up on net10.0 where the real framework types are used.

#### 0.2 — Enable analyzers globally for net10.0+

In `src/Directory.Build.props`, add:

```xml
<PropertyGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <IsAotCompatible>true</IsAotCompatible>
  <!-- IsAotCompatible implies IsTrimmable, EnableTrimAnalyzer, EnableAotAnalyzer, EnableSingleFileAnalyzer -->
</PropertyGroup>
```

This will immediately surface all IL2xxx/IL3xxx warnings on net10.0 builds across every source project. **Do not do this until Phase 1 annotations are ready**, or the build will be overwhelmed with warnings (treated as errors in CI).

**Alternative (incremental):** Enable per-project in the order listed in Phase 1-4, from least to most complex.

#### 0.3 — Add `IsAotCompatible` condition for netstandard2.0 ref-only builds

Since netstandard2.0 builds produce only reference assemblies, they don't need `IsAotCompatible`. But the attributes should still compile so the API surface is correct for consumers.

---

### Phase 1: `Microsoft.NET.StringTools` (Easiest)

**Target frameworks:** net472, net10.0, netstandard2.0, net35

**Why first:** No reflection, no dynamic code, pure string-interning utilities. This is the safest project to enable AOT on and validates the infrastructure from Phase 0.

**Steps:**
1. Add `<IsAotCompatible>true</IsAotCompatible>` (conditioned on net8.0+ compatible TFMs) to `StringTools.csproj`.
2. Build for net10.0 and fix any warnings (expected: zero or very few).
3. Run `StringTools.UnitTests` to verify.

**Estimated effort:** Minimal. Mostly infrastructure validation.

---

### Phase 2: `Microsoft.Build.Framework` (Low Complexity)

**Target frameworks:** net472, net10.0, netstandard2.0

**Why second:** Framework is the base dependency for all other MSBuild assemblies. It defines interfaces (`ITask`, `ILogger`, `IBuildEngine`) and event args. Mostly type definitions with limited reflection.

**Known reflection:**
- `ReflectableTaskPropertyInfo.cs` — Uses `Type.GetProperty()` with `BindingFlags`. This class wraps reflection over task properties.

**Steps:**
1. Enable `IsAotCompatible` in `Microsoft.Build.Framework.csproj` (conditioned on net8.0+).
2. Build for net10.0 and collect warnings.
3. Annotate `ReflectableTaskPropertyInfo`:
   - Add `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]` to the `Type` parameter in the constructor.
4. Review all public types and interfaces — add `[DynamicallyAccessedMembers]` to any type parameters used with reflection.
5. `MSBuildEventSource` — already AOT-safe (primitive `WriteEvent` only), no changes needed.
6. Update reference assembly if public API annotations changed.
7. Run `Framework.UnitTests`.

**Estimated effort:** Low. Mostly annotation flow on `ReflectableTaskPropertyInfo`.

---

### Phase 3: `Microsoft.Build.Utilities.Core` (Medium Complexity)

**Target frameworks:** net472, net10.0, netstandard2.0

**Why third:** Utilities depends on Framework and provides base classes for task authors (`Task`, `ToolTask`, `TaskLogging`). Some reflection for task parameter discovery.

**Known reflection:**
- `TaskLoggingHelper` — May use reflection for parameter validation.
- Utilities shared code may reference `TypeLoader`/`TaskLoader` patterns.

**Steps:**
1. Enable `IsAotCompatible` (conditioned on net8.0+).
2. Build for net10.0 and collect warnings.
3. Annotate methods that accept `Type` parameters with `[DynamicallyAccessedMembers]` where reflection is used on those types.
4. For methods that fundamentally require dynamic type loading, add `[RequiresUnreferencedCode]`.
5. Update reference assembly for public API annotation changes.
6. Run `Utilities.UnitTests`.

---

### Phase 4: `Microsoft.Build` (Core Engine — High Complexity)

**Target frameworks:** net472, net10.0

**Why fourth:** Core engine with the heaviest reflection usage. Plugin architecture (tasks, loggers, SDK resolvers) is fundamentally dynamic.

**Known reflection (critical paths):**

| File | Pattern | Annotation Strategy |
|------|---------|---------------------|
| `Shared/TypeLoader.cs` | `Type.GetType(name)`, `Assembly.LoadFrom()`, `GetExportedTypes()` | `[RequiresUnreferencedCode]` — dynamic loading by design |
| `Shared/TaskLoader.cs` | `Activator.CreateInstance(loadedType.Type)` | `[RequiresUnreferencedCode]` + `[DynamicallyAccessedMembers(PublicConstructors)]` on Type params |
| `Logging/LoggerDescription.cs` | `Activator.CreateInstance(loggerClass.Type)` | `[RequiresUnreferencedCode]` — logger loading is inherently dynamic |
| `BackEnd/SdkResolverLoader.cs` | `Activator.CreateInstance()`, `Assembly.Load` | `[RequiresUnreferencedCode]` — SDK resolver plugin loading |
| `Instance/TaskRegistry.cs` | `Type.GetType()`, `Activator.CreateInstance()` | `[RequiresUnreferencedCode]` on factory methods |
| `Evaluation/Expander.cs` | `Type.GetType()` for property functions | `[RequiresUnreferencedCode]` — evaluates arbitrary property functions |
| `BuildCheck/Acquisition/BuildCheckAcquisitionModule.cs` | `Assembly.LoadFrom`, `Activator.CreateInstance` | `[RequiresUnreferencedCode]` — plugin loading |
| `Utilities/NuGetFrameworkWrapper.cs` | `Assembly.Load()` for NuGet runtime | `[RequiresUnreferencedCode]` |
| `Shared/LogMessagePacketBase.cs` | `Type.GetMethod()` for event serialization | Evaluate if `[DynamicallyAccessedMembers]` can annotate, else `[RequiresUnreferencedCode]` |

**Strategy:**
The core MSBuild engine is a **plugin host** — it discovers, loads, and invokes arbitrary task/logger/SDK resolver types at runtime by name. This is **fundamentally incompatible with full AOT trimming**. The correct approach is:

1. **Annotate the plugin boundaries** with `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`:
   - `TypeLoader.Load()` and related methods
   - `TaskLoader.CreateTask()`
   - `LoggerDescription.CreateLogger()`
   - `SdkResolverLoader` loading methods
   - `BuildCheckAcquisitionModule` loading methods
2. **Propagate annotations** up the call chain to public API entry points that trigger dynamic loading (e.g., `BuildManager.Build()`, project evaluation methods).
3. **Annotate with `[DynamicallyAccessedMembers]`** where feasible:
   - `LoadedType.Type` should carry `[DynamicallyAccessedMembers(PublicConstructors | PublicProperties)]` since MSBuild needs constructors and property setters for tasks.
4. **Use `[UnconditionalSuppressMessage]`** only for verified-safe internal patterns where the annotation cannot be expressed (e.g., known-closed type sets).

**Steps:**
1. Enable `IsAotCompatible` (conditioned on net8.0+) in `Microsoft.Build.csproj`.
2. Build for net10.0 — expect a large number of warnings. Triage by warning code.
3. Start with innermost reflection calls (`TypeLoader`, `TaskLoader`) and annotate outward.
4. Group warnings by pattern and batch-fix:
   - All `Activator.CreateInstance` sites → add `[DynamicallyAccessedMembers]` on the Type or `[RequiresUnreferencedCode]` on the method
   - All `Type.GetType(string)` sites → `[RequiresUnreferencedCode]`
   - All `Assembly.Load`/`LoadFrom` sites → `[RequiresUnreferencedCode]`
5. Rebuild after every 10-15 annotations to catch cascading warnings.
6. Run `Build.UnitTests`.

**Estimated effort:** High. The majority of the work in this plan. May require 50-100+ annotations across 20+ files.

---

### Phase 5: `Microsoft.Build.Tasks.Core` (High Complexity)

**Target frameworks:** net472, net10.0, netstandard2.0

**Known reflection:**

| File | Pattern | Strategy |
|------|---------|----------|
| `TlbReference.cs` | `System.Reflection.Emit` — COM interop wrapper generation | `[RequiresDynamicCode]` on `ResolveComReference` task — **cannot be made AOT-safe** |
| `ManifestUtil/CngLightup.cs` | `MakeGenericType` for crypto delegates | `[RequiresDynamicCode]` or refactor to avoid generic construction |
| Various task implementations | `Type.GetType()`, `Activator.CreateInstance()` | Annotate per-task |

**Steps:**
1. Enable `IsAotCompatible` (conditioned on net8.0+) in `Microsoft.Build.Tasks.csproj`.
2. Build for net10.0 and triage warnings.
3. Mark `TlbReference` / `ResolveComReference` and related COM interop types with `[RequiresDynamicCode("Uses Reflection.Emit for COM interop wrapper generation.")]`.
4. Mark `CngLightup` MakeGenericType usages with `[RequiresDynamicCode]`.
5. Annotate remaining reflection patterns using the same approach as Phase 4.
6. Run `Tasks.UnitTests`.

---

### Phase 6: `MSBuild` CLI (Medium Complexity)

**Target frameworks:** net472, net10.0

**Known issues:**
- `JsonOutputFormatter.cs` — Uses runtime `JsonNode` serialization without source generation.
- `XMake.cs` — Logger and console creation, `Activator.CreateInstance` for `TerminalLogger`.

**Steps:**
1. Enable `IsAotCompatible` (conditioned on net8.0+) in `MSBuild.csproj`.
2. **Add JSON source generation** for `JsonOutputFormatter`:
   - Create a `JsonSerializerContext` subclass with `[JsonSerializable]` for serialized types.
   - Replace `JsonNode.ToJsonString()` calls with source-generated serialization if applicable, or accept this as AOT-safe since `JsonNode` serialization doesn't require type metadata.
3. Annotate `XMake.cs` logger creation paths with `[RequiresUnreferencedCode]`.
4. Run `MSBuild.UnitTests`.

---

## Annotation Strategy Summary

### When to use `[RequiresUnreferencedCode]`
- Methods that load types by name (`Type.GetType(string)`)
- Methods that load assemblies dynamically (`Assembly.Load`, `Assembly.LoadFrom`)
- Plugin-host methods that instantiate user-defined types
- Methods wrapping APIs already annotated with `[RequiresUnreferencedCode]`

### When to use `[RequiresDynamicCode]`
- Methods that use `Reflection.Emit` (TlbReference COM interop)
- Methods that use `MakeGenericType` / `MakeGenericMethod` with runtime-only types

### When to use `[DynamicallyAccessedMembers]`
- Type parameters that are reflected over for known member categories
- `LoadedType.Type` → `PublicConstructors | PublicProperties` (task instantiation + parameter setting)
- `ReflectableTaskPropertyInfo` constructor's Type parameter → `PublicProperties`

### When to use `[UnconditionalSuppressMessage]`
- Verified false positives where the reflected type is from a closed, known set
- Internal helper methods where all callers are already annotated

---

## Follow-Up Issues to File

1. **Investigate source-generated task/logger registration** — Long-term, MSBuild could support a source-generated mechanism for registering tasks/loggers instead of runtime discovery, enabling true AOT plugin support.
2. **COM interop AOT alternative** — `TlbReference.cs` uses `Reflection.Emit` and cannot be made AOT-safe. Investigate whether COM interop wrapper generation can move to a source generator or build-time tool.
3. **NuGet dependency AOT compatibility** — `NuGet.Build.Tasks` and `Microsoft.Build.NuGetSdkResolver` bring `Newtonsoft.Json` as a transitive dependency. Track upstream AOT work in NuGet. These are separate processes/assemblies and don't affect MSBuild's own AOT compatibility directly.
4. **Property function AOT safety** — `Expander.cs` uses `Type.GetType()` to resolve property function targets. Investigate whether a known-set optimization could avoid the `[RequiresUnreferencedCode]` annotation for built-in property functions while keeping it for user-defined ones.
5. **JSON source generation for CLI output** — Evaluate if `JsonOutputFormatter.cs` benefits from source generation or if `JsonNode` serialization is already AOT-safe enough.

---

## Validation Checklist

For each phase:
- [ ] Build succeeds on all TFMs with zero IL2xxx/IL3xxx warnings (net10.0)
- [ ] Build succeeds on net472 / netstandard2.0 / net35 (polyfill attributes compile)
- [ ] No new C# compiler warnings introduced
- [ ] Reference assemblies updated for any public API annotation changes
- [ ] Relevant unit tests pass
- [ ] Full repo build (`.\build.cmd -v quiet`) passes

Final validation:
- [ ] Full test suite passes (`.\build.cmd -test`)
- [ ] Bootstrap MSBuild builds sample project: `dotnet build src/Samples/Dependency/Dependency.csproj`
- [ ] `dotnet artifacts/bin/bootstrap/core/MSBuild.dll --help` works

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Annotation cascade makes public API surface noisy with `[RequiresUnreferencedCode]` | High | Medium | Accept that MSBuild's plugin architecture inherently requires dynamic code; document clearly |
| Polyfill attributes conflict with framework types | Low | High | Guard with `#if !NET5_0_OR_GREATER` and use `internal` visibility |
| Performance regression from annotation validation overhead | Very Low | Low | Annotations are metadata-only, no runtime cost |
| Breaking change — consumers see new warnings when calling annotated APIs | Medium | Medium | This is expected and desired behavior; the annotations tell the truth about AOT compatibility |
| Large number of cascading warnings overwhelms incremental delivery | Medium | Medium | Enable per-project, not globally. Fix Phase 1-2 first, validate, then proceed |

---

## Recommended Execution Order

```
Phase 0.1  (polyfills)           → PR #1
Phase 0.2  (per-project, not global yet)
Phase 1    (StringTools)         → PR #2 (includes IsAotCompatible in csproj)
Phase 2    (Framework)           → PR #3
Phase 3    (Utilities)           → PR #4
Phase 4    (Build - core engine) → PR #5 (largest, may split into sub-PRs)
Phase 5    (Tasks)               → PR #6
Phase 6    (MSBuild CLI)         → PR #7
Phase 0.2  (global enablement)   → PR #8 (move IsAotCompatible to Directory.Build.props)
```

Each PR should be independently buildable and testable. Phase 4 (core engine) is the most complex and may benefit from splitting into sub-PRs by subsystem (evaluation, back-end execution, logging, BuildCheck).
