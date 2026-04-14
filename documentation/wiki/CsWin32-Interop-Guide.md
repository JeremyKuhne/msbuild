# CsWin32 Interop Conversion Guide

This guide describes how to convert Win32 interop code in this repository to use CsWin32 with platform-specific conditional compilation. It is designed for AI coding agents performing incremental migration.

## Goals

1. **Eliminate runtime OS checks** — use `#if TARGET_WINDOWS` / `#if !TARGET_WINDOWS` to select code paths at compile time, not `if (IsWindows)` at runtime.
2. **Reduce binary size** — Windows builds should not carry Unix code (libc imports). Unix builds should not carry Windows interop code (DllImport, CsWin32 types).
3. **Enable AOT** — CsWin32 generates struct-based COM and direct P/Invoke that is AOT-compatible. Hand-written `[DllImport]` with marshaling is not.
4. **Use CsWin32 types directly** — callers should use `HANDLE`, `HMODULE`, `WIN32_FILE_ATTRIBUTE_DATA`, `MEMORYSTATUSEX`, etc. directly. Do not create wrapper types or re-define structs that CsWin32 already generates.

## Key Infrastructure

### `TARGET_WINDOWS` Define

The `TARGET_WINDOWS` preprocessor symbol is set in `Directory.Build.props` when `$(TargetOS) == 'windows'`. It is the **compile-time** equivalent of the runtime `NativeMethods.IsWindows` check. Use `#if TARGET_WINDOWS` instead of `if (IsWindows)` to exclude Windows-specific code from non-Windows builds entirely.

```xml
<!-- Directory.Build.props -->
<DefineConstants Condition="'$(TargetOS)' == 'windows'">$(DefineConstants);TARGET_WINDOWS</DefineConstants>
```

### CsWin32 Configuration

CsWin32 is configured in `Microsoft.Build.Framework`:

- **`NativeMethods.txt`** — list of Win32 APIs, interfaces, structs, enums, and constants to generate. CsWin32 pulls transitive dependencies automatically.
- **`NativeMethods.json`** — generator settings. Key settings:
  - `"allowMarshaling": false` — generates raw pointer-based signatures (AOT-safe).
  - `"useSafeHandles": false` — uses `HANDLE`/`HMODULE` structs, not `SafeHandle`.
  - `"comInterop": { "preserveSigMethods": ["*"] }` — COM methods return `HRESULT` directly.
- **Package reference** conditioned on `'$(TargetOS)' == 'windows'` — CsWin32 is not available on non-Windows builds.

### Namespaces

CsWin32 generates code in `Windows.Win32.*` and `Windows.Wdk.*` namespaces. Add `using` directives inside `#if TARGET_WINDOWS` to keep names short:

```csharp
#if TARGET_WINDOWS
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;
using Wdk = Windows.Wdk;
#endif
```

### `FEATURE_*` Defines Are Implicitly Windows-Only

The `FEATURE_*` preprocessor symbols (defined in `src/Directory.BeforeCommon.targets`) are conditioned on `$(TargetFramework.StartsWith('net4'))`, which only matches .NET Framework TFMs. Since .NET Framework only runs on Windows, **code inside `#if FEATURE_*` blocks is inherently Windows-only** — no additional `#if TARGET_WINDOWS` guard is needed.

```csharp
// GOOD: FEATURE_LEGACY_GETCURRENTDIRECTORY is net472-only, which is Windows-only
#if FEATURE_LEGACY_GETCURRENTDIRECTORY
    int bufferSize = (int)PInvoke.GetCurrentDirectory(0, null);
    // ... PInvoke calls are safe here without TARGET_WINDOWS guard ...
#else
    return Directory.GetCurrentDirectory();
#endif

// BAD: redundant TARGET_WINDOWS guard inside a FEATURE_* block
#if FEATURE_LEGACY_GETCURRENTDIRECTORY && TARGET_WINDOWS
    if (IsWindows) // ← triply redundant: FEATURE is net4, TARGET_WINDOWS is Windows, IsWindows is runtime check
    {
        // ...
    }
#endif
```

Key `FEATURE_*` defines that imply Windows-only:
- `FEATURE_LEGACY_GETCURRENTDIRECTORY` / `FEATURE_LEGACY_GETFULLPATH` — net4 perf optimizations using P/Invoke
- `FEATURE_FILE_TRACKER` — FileTracker.dll (Windows-only native DLL)
- `FEATURE_GAC`, `FEATURE_STRONG_NAMES` — GAC/fusion.dll
- `FEATURE_WIN32_REGISTRY`, `FEATURE_REGISTRY_TOOLSETS` — Windows registry
- `FEATURE_RESGEN`, `FEATURE_RESGENCACHE` — ResGen.exe (Windows SDK tool)
- `FEATURE_MSCOREE` — CLR hosting APIs

**Do not** add `TARGET_WINDOWS` guards to code already inside `#if FEATURE_*` — it adds noise without value.

### `#if TARGET_WINDOWS` vs `#if NET && TARGET_WINDOWS`

`TARGET_WINDOWS` is defined for **all TFMs** on Windows builds (net472, netstandard2.0, net10.0). However, many CsWin32 helper types — COM structs with `delegate* unmanaged`, `ComScope<T>` ref structs, WMI interfaces using `static abstract` — require .NET 7+ language features that do not compile on net472 or netstandard2.0.

**Rule:** Use `#if NET && TARGET_WINDOWS` for code that needs **both** CsWin32 types **and** modern .NET features (function pointers, `static abstract`, `ref struct` improvements).

| Guard | When to use |
|-------|-------------|
| `#if TARGET_WINDOWS` | P/Invoke wrappers, registry access, CsWin32 struct types (e.g. `WIN32_FILE_ATTRIBUTE_DATA`, `HANDLE`) — these work on all TFMs |
| `#if NET && TARGET_WINDOWS` | COM struct implementations with `delegate* unmanaged` vtables, `ComScope<T>`, WMI interfaces, `VARIANT` with `IDisposable`, anything using `IComIID` with `static abstract` |
| `#if TARGET_WINDOWS && !NETSTANDARD` | Shared helpers like `IID.Get<T>()` that need CsWin32 types but not `static abstract` (net472 has [instance-based `IComIID`](../../src/Framework/Framework/Windows/Win32/IComIID.cs)) |

**Example — WMI COM struct (needs `#if NET && TARGET_WINDOWS`):**
```csharp
#if NET && TARGET_WINDOWS

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Microsoft.Build.Shared.Win32.Wmi;

internal unsafe struct IWbemLocator : IComIID
{
    // ... delegate* unmanaged vtable methods ...
}

#endif
```

**Example — P/Invoke wrapper (needs only `#if TARGET_WINDOWS`):**
```csharp
#if TARGET_WINDOWS
[SupportedOSPlatform("windows")]
internal static int GetShortPathName(string path, char[] fullpath, int length)
{
    Span<char> span = fullpath is null ? default : fullpath.AsSpan(0, length);
    return (int)PInvoke.GetShortPathName(path, span);
}
#endif
```

## Conversion Patterns

### Pattern 1: Replace `[DllImport]` with `PInvoke.*`

**Before:**
```csharp
[DllImport("kernel32.dll", SetLastError = true)]
internal static extern bool CloseHandle(IntPtr hObject);
```

**After:**
```csharp
// No wrapper needed — callers use PInvoke.CloseHandle directly
PInvoke.CloseHandle((HANDLE)handle);
```

**Key rule:** If a `PInvoke.*` method is a simple 1-2 line delegation, do not wrap it. Have callers call `PInvoke.*` directly with CsWin32 types. Only wrap when the signature adaptation is complex (multiple `fixed` blocks, struct layout translation, etc.).

### Pattern 2: Replace `if (IsWindows)` with `#if TARGET_WINDOWS`

**Before:**
```csharp
internal static bool DirectoryExists(string fullPath)
{
    return IsWindows
        ? DirectoryExistsWindows(fullPath)
        : Directory.Exists(fullPath);
}
```

**After:**
```csharp
internal static bool DirectoryExists(string fullPath)
{
#if TARGET_WINDOWS
    return DirectoryExistsWindows(fullPath);
#else
    return Directory.Exists(fullPath);
#endif
}
```

**Do not** mix `#if TARGET_WINDOWS` and `if (IsWindows)` — if you're already inside a `#if TARGET_WINDOWS` block, the runtime check is redundant. Guarding with `if (IsWindows)` inside `#if TARGET_WINDOWS` generates dead code (the condition is always true) and confuses readers.

### Pattern 3: Condition-out Windows-only code

**Individual methods** that are exclusively Windows (e.g., process tree management, registry access) go inside `#if TARGET_WINDOWS`:

```csharp
#if TARGET_WINDOWS
[SupportedOSPlatform("windows")]
internal static MEMORYSTATUSEX? GetMemoryStatus()
{
    MEMORYSTATUSEX status = default;
    status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
    if (!PInvoke.GlobalMemoryStatusEx(ref status))
    {
        return null;
    }

    return status;
}
#endif
```

Callers must also be conditioned:
```csharp
#if TARGET_WINDOWS
var memoryStatus = NativeMethodsShared.GetMemoryStatus();
// ... use memoryStatus ...
#endif
```

**Entire Windows-only files** should be excluded at the project level, not wrapped in `#if` / `#endif`. This avoids boilerplate, keeps the source cleaner, and prevents issues like IDE0005 (unnecessary using) or CS1587 (orphaned XML comment) that arise when an entire file is conditionally compiled.

Use `<Compile Remove>` in the `.csproj` with a condition on `$(TargetOS)`:

```xml
<!-- Exclude Windows-only source files from non-Windows builds -->
<ItemGroup Condition="'$(TargetOS)' != 'windows'">
  <Compile Remove="FileSystem\WindowsFileSystem.cs" />
  <Compile Remove="FileSystem\MSBuildOnWindowsFileSystem.cs" />
</ItemGroup>
```

For files that additionally require modern .NET (e.g., `delegate* unmanaged`, `static abstract`), combine the conditions:

```xml
<!-- WMI COM structs and VARIANT require both .NET and Windows -->
<ItemGroup Condition="'$(TargetOS)' != 'windows' OR '$(TargetFrameworkIdentifier)' != '.NETCoreApp'">
  <Compile Remove="Utilities\VARIANT.cs" />
  <Compile Remove="Utilities\Wmi\*.cs" />
</ItemGroup>
```

For Shared files included via `<Compile Include>`, condition the include itself:

```xml
<!-- Only compile for .NET + Windows -->
<ItemGroup Condition="'$(TargetOS)' == 'windows' AND '$(TargetFrameworkIdentifier)' == '.NETCoreApp'">
  <Compile Include="..\Shared\Win32\ComScope.cs" Link="Shared\Win32\ComScope.cs" />
</ItemGroup>
```

**Do not** wrap entire files in `#if TARGET_WINDOWS` / `#endif` — use project-level exclusions instead.

**Factory methods** that select between Windows and non-Windows implementations use `#if TARGET_WINDOWS` at the call site:

```csharp
private static IFileSystem GetFileSystem()
{
#if TARGET_WINDOWS
    return MSBuildOnWindowsFileSystem.Singleton();
#else
    return ManagedFileSystem.Singleton();
#endif
}
```

### Pattern 4: Cross-platform methods with platform-specific implementations

For methods needed on all platforms but with different implementations:

```csharp
internal static bool MakeSymbolicLink(string newFileName, string existingFileName, ref string errorMessage)
{
#if TARGET_WINDOWS
    bool created = PInvoke.CreateSymbolicLink(newFileName, existingFileName, flags);
    errorMessage = created ? null : Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()).Message;
#else
    bool created = symlink(existingFileName, newFileName) == 0;
    errorMessage = created ? null : Marshal.GetLastWin32Error().ToString();
#endif
    return created;
}
```

**When non-Windows behavior is a no-op or passthrough**, make the method Windows-only and push the `#if TARGET_WINDOWS` guard to the **caller**. This makes it explicit that the operation is a Windows concept, and allows the platform conditional to be rolled further upstream over time:

```csharp
// Method is Windows-only — no cross-platform wrapper with a silent passthrough
#if TARGET_WINDOWS
[SupportedOSPlatform("windows")]
internal static string GetShortFilePath(string path)
{
    if (path != null)
    {
        int length = GetShortPathName(path, null, 0);
        // ... Windows path shortening logic ...
    }
    return path;
}
#endif
```

The **caller** owns the platform decision:
```csharp
// Caller guards the Windows-only call and provides the non-Windows behavior inline
if (batchFileForCommandLine.Contains("&") && !batchFileForCommandLine.Contains("^&"))
{
#if TARGET_WINDOWS
    batchFileForCommandLine = NativeMethodsShared.GetShortFilePath(batchFileForCommandLine);
#endif
    batchFileForCommandLine = batchFileForCommandLine.Replace("&", "^&");
}
```

**Do not** hide a no-op behind a cross-platform method. If the non-Windows behavior is "return the input unchanged" or "do nothing", the method should not exist on non-Windows at all:

```csharp
// AVOID: cross-platform wrapper that silently does nothing on non-Windows
internal static string GetShortFilePath(string path)
{
#if TARGET_WINDOWS
    // ... Windows implementation ...
#endif
    return path;  // ← caller can't tell this is a no-op
}
```

This principle pushes platform awareness toward callers, which:
- Makes Windows-only semantics visible at the call site
- Allows rolling `#if TARGET_WINDOWS` conditionals further upstream
- Produces cleaner non-Windows binaries (no dead method bodies)
- Enables the compiler to flag unguarded calls to Windows-only methods via CA1416

**Deciding between cross-platform and Windows-only:**

| Non-Windows behavior | Approach |
|---------------------|----------|
| Substantive platform-specific logic (libc calls, `/proc/` reads) | `#if TARGET_WINDOWS` / `#else` with real code on both sides |
| No-op / passthrough / return default | Make method Windows-only; guard at call site |
| No callers outside Windows code paths | Make the method Windows-only (exclude from build, see [Pattern 3](#pattern-3-condition-out-windows-only-code)) |
| Returns a sentinel like `NotApplicable` from an enum | `#if TARGET_WINDOWS` / `#else return NotApplicable; #endif` — keep the `#else` since the sentinel has semantic meaning |

When a method has **three or more branches** (Windows, non-Windows .NET, .NET Framework), use `#elif` chains rather than nested `#if` blocks. Nested blocks cause CS0161 ("not all code paths return a value") on TFMs where none of the inner blocks are active:

```csharp
// GOOD: #elif chain — exactly one branch is active on every TFM
public static bool TryGetCommandLine(this Process? process, out string? commandLine)
{
    // ...
    try
    {
#if NET && TARGET_WINDOWS
        commandLine = Windows.GetCommandLine(process.Id);
        return true;
#elif NET
        if (NativeMethods.IsLinux)
        {
            commandLine = Linux.GetCommandLine(process.Id);
            return true;
        }
        // ... other OS branches ...
#else
        // .NET Framework fallback
        commandLine = null;
        return true;
#endif
    }
    catch { return false; }
}

// BAD: nested #if — leaves net472 with no return path
try
{
#if NET
#if TARGET_WINDOWS
    commandLine = Windows.GetCommandLine(process.Id);
    return true;
#else
    // ... non-Windows .NET ...
#endif
    // This code is unreachable on TARGET_WINDOWS, dead on !NET:
    commandLine = null;
    return true;
#endif  // net472 has no code path at all → CS0161
}
```

### Pattern 5: Use CsWin32 string overloads

CsWin32 generates convenience overloads that accept `string` and `Span<char>` instead of requiring `fixed` and raw pointers:

**Before (manual pinning):**
```csharp
internal static unsafe int GetShortPathName(string path, char[] fullpath, int length)
{
    fixed (char* pathPtr = path)
    {
        fixed (char* fullpathPtr = fullpath)
        {
            return (int)PInvoke.GetShortPathName(pathPtr, fullpathPtr, (uint)length);
        }
    }
}
```

**After (Span overload):**
```csharp
internal static int GetShortPathName(string path, char[] fullpath, int length)
{
    Span<char> span = fullpath is null ? default : fullpath.AsSpan(0, length);
    return (int)PInvoke.GetShortPathName(path, span);
}
```

Similarly:
- `PInvoke.GetModuleFileName(HMODULE, Span<char>)` — no need to pin `char[]`
- `PInvoke.SetCurrentDirectory(string)` — no need for `fixed (char* p = path)`
- `PInvoke.GetConsoleMode(HANDLE, out CONSOLE_MODE)` — `out` overload instead of pointer
- `PInvoke.GetFullPathName(string, Span<char>, PWSTR*)` — string + span overload

### Pattern 6: Use CsWin32 types in public/internal signatures

Do not wrap CsWin32 types. Let callers use them directly:

**Avoid:**
```csharp
// Don't create wrappers
internal struct MemoryStatus
{
    private MEMORYSTATUSEX _inner;
    public uint dwMemoryLoad => _inner.dwMemoryLoad;
    // ...
}
```

**Preferred:**
```csharp
// Return the CsWin32 type directly
internal static MEMORYSTATUSEX? GetMemoryStatus() { ... }
```

Since the outer class (`NativeMethods`) is `internal`, all nested types are effectively internal — there is no API compatibility concern.

### Pattern 7: WDK (Nt*) APIs

CsWin32 includes WDK metadata. Nt APIs are in the `Windows.Wdk` namespace:

```csharp
using Wdk = Windows.Wdk;
using WdkThreading = Windows.Wdk.System.Threading;

// Usage:
NTSTATUS status = Wdk.PInvoke.NtQueryInformationProcess(
    (HANDLE)hProcess.DangerousGetHandle(),
    WdkThreading.PROCESSINFOCLASS.ProcessBasicInformation,
    pbiPtr,
    (uint)sizeof(PROCESS_BASIC_INFORMATION),
    ref returnLength);
```

Add the API name to `NativeMethods.txt` — CsWin32 resolves it from the WDK metadata automatically. The generated types go into `Windows.Win32.System.Threading` (for `PROCESS_BASIC_INFORMATION`) and `Windows.Wdk.System.Threading` (for `PROCESSINFOCLASS`).

## Common Gotchas

### 1. CA1416 platform compatibility warnings

CsWin32-generated methods carry `[SupportedOSPlatform("windows5.x.xxxx")]` attributes. Callers inside `#if TARGET_WINDOWS` may trigger CA1416 because the analyzer does not understand `#if` guards — it only recognizes TFM platform suffixes (e.g., `net10.0-windows`), `[SupportedOSPlatform]` attributes, and runtime `OperatingSystem.IsWindows()` guards.

**Why `[assembly: SupportedOSPlatform("windows")]` does not work here:** MSBuild assemblies are cross-platform — marking the entire assembly as Windows-only would cascade CA1416 errors to all consumers calling any MSBuild API. The assembly-level attribute is only appropriate for assemblies that exclusively target Windows.

**Preferred approach:** Suppress CA1416 at the project level in `<NoWarn>` for projects that contain CsWin32 interop:

```xml
<!-- CS3016: CsWin32-generated code uses arrays as attribute arguments (not CLS-compliant).
     CA1416: CsWin32-generated P/Invoke methods are platform-specific; callers are already guarded. -->
<NoWarn>$(NoWarn);CS3016;CA1416</NoWarn>
```

For files in other projects that occasionally call Windows-only APIs, use `#pragma warning disable CA1416` scoped to the minimum necessary region:

```csharp
#if TARGET_WINDOWS
#pragma warning disable CA1416
    PInvoke.CloseHandle((HANDLE)processInfo.hProcess);
#pragma warning restore CA1416
#endif
```

### 2. `FILETIME` type confusion

CsWin32 uses `System.Runtime.InteropServices.ComTypes.FILETIME` (has `int` fields), not `Windows.Win32.Foundation.FILETIME` (has `uint` fields). The `PInvoke.GetFileTime` method takes `ComTypes.FILETIME*`. Be aware of which `FILETIME` you're using.

### 3. Nullable struct parameters

CsWin32 generates `SECURITY_ATTRIBUTES?` (nullable struct) for optional parameters, not `SECURITY_ATTRIBUTES*` (pointer). Pass `null` for the nullable, not cast a pointer:

```csharp
// Correct
PInvoke.CreateFile(name, access, share, (SECURITY_ATTRIBUTES?)null, disposition, flags, HANDLE.Null);

// Wrong — won't compile
PInvoke.CreateFile(name, access, share, (SECURITY_ATTRIBUTES*)IntPtr.Zero, ...);
```

### 4. `BOOL` vs `bool`

CsWin32's `PInvoke.*` methods return `BOOL` (a struct), not `bool`. In most contexts the implicit conversion works, but in some patterns (e.g., checking `.Value`) you need to be explicit. The `BOOL` struct has an implicit conversion to `bool`.

### 5. `HANDLE` arithmetic

CsWin32's `HANDLE` struct wraps `IntPtr`. Convert with `(HANDLE)intPtr` or `(IntPtr)handle.Value`. For `HANDLE.Null` and `HANDLE.INVALID_HANDLE_VALUE`, use the static properties.

### 6. Enum casting

CsWin32 generates strongly-typed enums. Cast from `int`/`uint`:
```csharp
PInvoke.GetStdHandle((STD_HANDLE)nStdHandle);
PInvoke.SetThreadErrorMode((THREAD_ERROR_MODE)newMode, &oldMode);
```

For flag enums, use `.HasFlag()`:
```csharp
consoleMode.HasFlag(CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING)
```

### 7. `SafeFileHandle` vs `HANDLE`

CsWin32 returns `HANDLE` from most APIs. When you need a `SafeFileHandle` (for `using` disposal), construct one manually:
```csharp
HANDLE h = PInvoke.CreateFile(...);
return new SafeFileHandle((IntPtr)h.Value, ownsHandle: true);
```

When passing a `SafeFileHandle` to a CsWin32 API, use `DangerousGetHandle()`:
```csharp
PInvoke.GetFileTime((HANDLE)handle.DangerousGetHandle(), ...);
```

### 8. `DefaultItemExcludes` for multi-TFM polyfill folders

If you create a `Framework/` folder for net472-only polyfill code (e.g., instance-based `IComIID`), exclude it from non-net472 builds using `DefaultItemExcludes`. Use `$(TargetFramework)` not `$(TargetFrameworkIdentifier)` — the latter is not set during property evaluation:

```xml
<PropertyGroup Condition="'$(TargetFramework)' != '$(FullFrameworkTFM)'">
  <DefaultItemExcludes>$(DefaultItemExcludes);**/Framework/**/*</DefaultItemExcludes>
</PropertyGroup>
```

For Windows-only files, prefer `<Compile Remove>` conditioned on `$(TargetOS)` over `DefaultItemExcludes` — it's more explicit and doesn't require separate `<None Include>` items to keep files visible in Solution Explorer. See [Pattern 3](#pattern-3-condition-out-windows-only-code) for the preferred approach.

## Folding to Call Sites

The main optimization is to **eliminate indirection layers**. When a NativeMethods wrapper is just `return PInvoke.SomeApi(cast1, cast2)`, remove it and have callers use `PInvoke.SomeApi` directly.

**Decision tree for each wrapper method:**

1. **No external callers?** → Remove the wrapper entirely.
2. **1-2 callers, simple cast?** → Inline into callers. Add `using` directives and `#if TARGET_WINDOWS` at the call site.
3. **Many callers but straightforward?** → Keep as a thin wrapper in NativeMethods.cs, using CsWin32 types in its signature.
4. **Complex adaptation** (multiple `fixed` blocks, struct translation, error handling)? → Keep as a wrapper.

When inlining, add CsWin32 `using` directives to the calling file:
```csharp
#if TARGET_WINDOWS
using Windows.Win32;
using Windows.Win32.Foundation;
#endif
```

## Removing Unreachable Code

After converting to `#if TARGET_WINDOWS`:

1. **Dead constants** — if a constant was only used in runtime `if (IsWindows)` checks that are now compile-time, remove it. Examples: `GENERIC_READ`, `FILE_SHARE_READ`, `OPEN_EXISTING`, `INFINITE`, `WAIT_*`.
2. **Dead enums** — `StreamHandleType`, `PROCESSINFOCLASS`, `eDesiredAccess`, etc. are replaced by CsWin32 enums.
3. **Dead structs** — `SYSTEM_INFO`, `PROCESS_BASIC_INFORMATION`, `WIN32_FILE_ATTRIBUTE_DATA`, `MemoryStatus`, `SecurityAttributes` are replaced by CsWin32 types.
4. **Dead using directives** — remove `using` for `System.Reflection` (if `GetTypeInfo` is gone), `Microsoft.Win32.SafeHandles` (if all `SafeHandle` usage is CsWin32), etc.
5. **`IsWindows` property** — still needed for runtime checks in cross-platform code (e.g., `OSName`), but most interop code should use `#if TARGET_WINDOWS` instead.

### IDE0005: Unnecessary using directives

Official builds treat **all warnings as errors**, including IDE0005 (unnecessary `using` directives). When code moves behind `#if TARGET_WINDOWS`, some `using` directives become unnecessary on non-Windows builds.

**Condition the `using` with the same guard:**
```csharp
#if TARGET_WINDOWS
using Windows.Win32;
using Windows.Win32.Foundation;
#endif
```

**Watch for `using` directives that become conditionally needed.** For example, `using Microsoft.Build.Framework.Logging;` is only needed by `AnsiDetector` in the `#else` (non-Windows) branch of `QueryIsScreenAndTryEnableAnsiColorCodes`. Guard it with `#if !TARGET_WINDOWS`.

### XML comments before `#if` blocks

XML doc comments placed **before** a `#if TARGET_WINDOWS` block produce CS1587 ("XML comment is not placed on a valid language element") on non-Windows builds because the comment has no code to attach to. Move the comment **inside** the block:

```csharp
// BAD — CS1587 on non-Windows
/// <summary>
/// Convert SYSTEM_INFO architecture values.
/// </summary>
#if TARGET_WINDOWS
private static ProcessorArchitectures ConvertSystemArchitecture(PROCESSOR_ARCHITECTURE arch) { ... }
#endif

// GOOD — comment is inside the conditional block
#if TARGET_WINDOWS
/// <summary>
/// Convert SYSTEM_INFO architecture values.
/// </summary>
private static ProcessorArchitectures ConvertSystemArchitecture(PROCESSOR_ARCHITECTURE arch) { ... }
#endif
```

## Project Configuration

### Framework.csproj

```xml
<!-- CsWin32 only on Windows -->
<PackageReference Include="Microsoft.Windows.CsWin32" Condition="'$(TargetOS)' == 'windows'">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>

<!-- PolySharp for net472 C# polyfills (only needed with CsWin32 on Windows) -->
<PackageReference Include="PolySharp" Condition="'$(TargetOS)' == 'windows' AND '$(TargetFramework)' == '$(FullFrameworkTFM)'">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>

<!-- Suppress CsWin32 analyzer warnings -->
<NoWarn>$(NoWarn);CS3016;CA1416</NoWarn>
```

## Reference Implementations

| Repository | Pattern | Link |
|-----------|---------|------|
| dotnet/sdk | Full CsWin32 migration with COM, ComScope, IComIID | [PR #52813](https://github.com/dotnet/sdk/pull/52813) |
| dotnet/sdk | `TargetOS`/`TARGET_WINDOWS` define infrastructure | [PR #53627](https://github.com/dotnet/sdk/pull/53627) |
| dotnet/winforms | Multi-targeting with CsWin32 for net472 | [PR #14384](https://github.com/dotnet/winforms/pull/14384) |
| JeremyKuhne/madowaku | CsWin32 multi-TFM (net10.0 + net472) | [madowaku](https://github.com/JeremyKuhne/madowaku) |
| MSBuild (this repo) | Existing CsWin32 COM migration guide | [CsWin32-COM-Interop-Migration.md](CsWin32-COM-Interop-Migration.md) |
