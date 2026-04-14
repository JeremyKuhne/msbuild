# Migrating from `[ComImport]` to CsWin32 COM Interop

This guide explains how to replace hand-written `[ComImport]` COM interface definitions and `[DllImport]` P/Invoke declarations with [CsWin32](https://github.com/microsoft/CsWin32)-generated bindings when multi-targeting .NET Framework and modern .NET. It is targeted at AI agents updating legacy interop code.

## Reference PRs

These PRs demonstrate the migration in production repositories. Use them as exemplars:

| PR | Repository | What it shows |
|----|-----------|---------------|
| [dotnet/sdk#52813](https://github.com/dotnet/sdk/pull/52813) | dotnet/sdk | Full CsWin32 migration: COM interfaces, P/Invoke, job objects, `ComScope<T>`, `BSTR`, `ComClassFactory`, `IComIID` bridge for .NET Framework |
| [dotnet/sdk#52822](https://github.com/dotnet/sdk/pull/52822) | dotnet/sdk | `LibraryImport` source-generated P/Invoke for hostfxr, custom `PlatformStringMarshaller` |
| [dotnet/winforms#14384](https://github.com/dotnet/winforms/pull/14384) | dotnet/winforms | Multi-targeting `System.Private.Windows.Core` for .NET Framework 4.7.2 with CsWin32, `IComIID` partials on Framework |

## 1. Taking the CsWin32 Dependency

### 1.1 Add the NuGet Package

Add `Microsoft.Windows.CsWin32` in your project file. It is a source-generator-only package, so mark it with `PrivateAssets="all"`:

```xml
<PackageReference Include="Microsoft.Windows.CsWin32"
                  Condition="'$(DotNetBuildSourceOnly)' != 'true'">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

> **Source:** [sdk `Microsoft.DotNet.Cli.Utils.csproj`][sdk-csproj] — lines adding CsWin32

If you use centralized package management, add the version to `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.183" />
```

### 1.2 Add PolySharp for .NET Framework Polyfills

When multi-targeting, modern C# features (`CallerArgumentExpression`, `OverloadResolutionPriority`, etc.) are missing on .NET Framework. [PolySharp](https://github.com/Sergio0694/PolySharp) provides source-generated polyfills:

```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net472'">
  <PackageReference Include="PolySharp">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>

<PropertyGroup>
  <PolySharpUsePublicAccessibilityForGeneratedTypes>false</PolySharpUsePublicAccessibilityForGeneratedTypes>
  <!-- CsWin32 also generates some polyfill types; exclude duplicates -->
  <PolySharpExcludeGeneratedTypes>
    System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute;
    System.Diagnostics.CodeAnalysis.UnscopedRefAttribute
  </PolySharpExcludeGeneratedTypes>
</PropertyGroup>
```

> **Source:** [sdk `Microsoft.DotNet.Cli.Utils.csproj`][sdk-csproj] — PolySharp section

## 2. Configuring CsWin32

CsWin32 requires two files in the project directory (next to the `.csproj`):

### 2.1 `NativeMethods.json` — Generator Settings

```json
{
  "$schema": "https://aka.ms/CsWin32.schema.json",
  "allowMarshaling": false,
  "useSafeHandles": false,
  "comInterop": {
    "preserveSigMethods": [
      "*"
    ]
  }
}
```

> **Source:** [sdk `NativeMethods.json`][sdk-nm-json] · [winforms `NativeMethods.json`][wf-nm-json]

Key settings explained:

| Setting | Value | Why |
|---------|-------|-----|
| `allowMarshaling` | `false` | Disables runtime marshalling. Produces raw pointer-based COM structs instead of RCW-managed `[ComImport]` interfaces. This is required for Native AOT compatibility and avoids hidden allocations. |
| `useSafeHandles` | `false` | Uses raw `HANDLE` / `HWND` structs instead of wrapped `SafeHandle` types, giving you direct control over lifetime. |
| `comInterop.preserveSigMethods` | `["*"]` | Makes all COM methods return `HRESULT` directly (via `[PreserveSig]`) instead of throwing on failure. You check `hr.Failed` / `hr.ThrowOnFailure()` yourself. This matches the native calling convention and avoids hidden exception costs. |

**Optional:** Set `"className"` to change the generated static class name (WinForms uses `"PInvokeCore"`; default is `"PInvoke"`).

### 2.2 `NativeMethods.txt` — What to Generate

List the functions, interfaces, structs, constants, and enums you need, one per line:

```
// This file is used by Microsoft.Windows.CsWin32 to generate
// all the native structs and API calls.

// Methods
AssignProcessToJobObject
CloseHandle
CoCreateInstance
CoGetClassObject
CreateJobObject
SetInformationJobObject
SendMessageTimeout

// Interfaces
IClassFactory
IInternetSecurityManager

// Structs, constants, enums
BSTR
HRESULT
JOBOBJECT_EXTENDED_LIMIT_INFORMATION
PROCESS_BASIC_INFORMATION
VARIANT
```

> **Source:** [sdk `NativeMethods.txt`][sdk-nm-txt] · [winforms `NativeMethods.txt`][wf-nm-txt]

CsWin32 automatically pulls in transitive dependencies (e.g. requesting `CoCreateInstance` also generates `CLSCTX`, `IUnknown`, etc.).

## 3. Project File Configuration for Multi-Targeting

### 3.1 Excluding Framework-Only Polyfills from .NET Builds

Place .NET Framework-only code in a `Framework/` subfolder and exclude it from modern .NET builds:

```xml
<PropertyGroup Condition="'$(TargetFramework)' != 'net472'">
  <IsAotCompatible>true</IsAotCompatible>
  <!-- Exclude Framework folder from non-.NET Framework builds -->
  <DefaultItemExcludes>$(DefaultItemExcludes);**/Framework/**/*</DefaultItemExcludes>
</PropertyGroup>

<!-- Still show Framework files in Solution Explorer -->
<ItemGroup Condition="'$(TargetFramework)' != 'net472'">
  <None Include="**/Framework/**/*" />
</ItemGroup>
```

### 3.2 Source-Build Exclusions

For Linux/macOS source-build scenarios where Windows-only code is irrelevant:

```xml
<PropertyGroup>
  <DefineConstants Condition="'$(DotNetBuildSourceOnly)' == 'true'">
    $(DefineConstants);DOTNET_BUILDSOURCEONLY
  </DefineConstants>
</PropertyGroup>

<ItemGroup Condition="'$(DotNetBuildSourceOnly)' == 'true'">
  <Compile Remove="Windows\**" />
  <Compile Remove="DangerousFileDetector.cs" />
</ItemGroup>
```

### 3.3 Global Usings

Set up common imports for the CsWin32-generated namespaces:

```csharp
// GlobalUsings.cs
#pragma warning disable IDE0005
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;

#if !DOTNET_BUILDSOURCEONLY
global using Windows.Win32;
global using Windows.Win32.Foundation;
#endif
#pragma warning restore IDE0005
```

> **Source:** [sdk `GlobalUsings.cs`][sdk-globalusings]

## 4. Key Patterns

### 4.1 `ComScope<T>` — Scoped COM Pointer Lifetime

The `ComScope<T>` `ref struct` wraps a native COM pointer and calls `Release()` on `Dispose()`. Use it in a `using` statement to guarantee cleanup:

```csharp
internal readonly unsafe ref struct ComScope<T> where T : unmanaged, IComIID
{
    private readonly nint _value;

    public T* Pointer => (T*)_value;
    public ComScope(T* value) => _value = (nint)value;
    public bool IsNull => _value == 0;

    // Implicit conversions for passing to out-parameters
    public static implicit operator T**(in ComScope<T> scope) =>
        (T**)Unsafe.AsPointer(ref Unsafe.AsRef(in scope._value));
    public static implicit operator void**(in ComScope<T> scope) =>
        (void**)Unsafe.AsPointer(ref Unsafe.AsRef(in scope._value));

    public ComScope<TInterface> TryQueryInterface<TInterface>(out HRESULT result)
        where TInterface : unmanaged, IComIID
    {
        Guid iid = IID.Get<TInterface>();
        ComScope<TInterface> scope = new(null);
        result = ((IUnknown*)Pointer)->QueryInterface(&iid, scope);
        return scope;
    }

    public void Dispose()
    {
        IUnknown* unknown = (IUnknown*)_value;
        *(void**)this = null;  // Null out to prevent double-release
        if (unknown is not null)
        {
            unknown->Release();
        }
    }
}
```

**Usage:**

```csharp
using var securityManager = factory.TryCreateInstance<IInternetSecurityManager>(out HRESULT hr);
if (hr.Failed)
{
    return false;
}

hr = securityManager.Pointer->MapUrlToZone(filename, out uint zone, PInvoke.MUTZ_ISFILE);
```

> **Source:** [sdk `ComScope{T}.cs`][sdk-comscope] · [sdk `DangerousFileDetector.cs`][sdk-dfd]

### 4.2 `ComClassFactory` — Creating COM Objects Without `Activator.CreateInstance`

Instead of `Type.GetTypeFromCLSID()` + `Activator.CreateInstance()`, use `CoGetClassObject` via a wrapper:

```csharp
internal sealed unsafe class ComClassFactory : IDisposable
{
    private readonly IClassFactory* _classFactory;

    public static bool TryCreate(
        Guid classId,
        [NotNullWhen(true)] out ComClassFactory? factory,
        out HRESULT result)
    {
        IClassFactory* classFactory;
        Guid iid = IClassFactory.IID_Guid;
        result = PInvoke.CoGetClassObject(
            &classId,
            CLSCTX.CLSCTX_INPROC_SERVER,
            (void*)null,
            &iid,
            (void**)&classFactory);

        if (result.Failed || classFactory is null)
        {
            factory = null;
            return false;
        }

        factory = new ComClassFactory(classFactory);
        return true;
    }

    public ComScope<TInterface> TryCreateInstance<TInterface>(out HRESULT result)
        where TInterface : unmanaged, IComIID
    {
        Guid iid = IID.Get<TInterface>();
        ComScope<TInterface> scope = default;
        result = _classFactory->CreateInstance(null, &iid, scope);
        return scope;
    }

    public void Dispose() => _classFactory->Release();
}
```

**Before (old pattern):**

```csharp
Type? iismType = Type.GetTypeFromCLSID(new Guid(CLSID_InternetSecurityManager));
internetSecurityManager = Activator.CreateInstance(iismType) as IInternetSecurityManager;
```

**After (CsWin32 pattern):**

```csharp
if (!ComClassFactory.TryCreate(CLSID.InternetSecurityManager, out var factory, out HRESULT result))
{
    if (result != HRESULT.REGDB_E_CLASSNOTREG)
        result.ThrowOnFailure();
    return false;
}

using var securityManager = factory.TryCreateInstance<IInternetSecurityManager>(out HRESULT hr);
```

> **Source:** [sdk `ComClassFactory.cs`][sdk-comfactory] · [sdk `DangerousFileDetector.cs`][sdk-dfd]

### 4.3 `BSTR` — Managing COM Strings

CsWin32 generates a `BSTR` struct. Add a partial to implement `IDisposable` and safe accessors:

```csharp
internal readonly unsafe partial struct BSTR : IDisposable
{
    public BSTR(string value) : this((char*)Marshal.StringToBSTR(value)) { }

    public void Dispose()
    {
        Marshal.FreeBSTR((nint)Value);
        Unsafe.AsRef(in this) = default;
    }

    public bool IsNull => Value is null;
}
```

**Usage:**

```csharp
using BSTR name = new("CommandLine");
hr = obj->Get(name, 0, ref val, IntPtr.Zero, IntPtr.Zero);
```

> **Source:** [sdk `BSTR.cs`][sdk-bstr]

### 4.4 `IComIID` — Bridging .NET Framework and .NET

CsWin32 generates `IComIID` with `static abstract` members on .NET 6+. On .NET Framework, `static abstract` is not available. The solution: define a non-static `IComIID` interface for Framework and use `#if` in the helper:

**Framework-compatible `IComIID`:**

```csharp
namespace Windows.Win32;

/// <summary>
///  Common interface for COM interface wrapping structs.
/// </summary>
/// <remarks>
///  On .NET 6+, this is provided by CsWin32 as a static abstract.
/// </remarks>
public interface IComIID
{
    Guid Guid { get; }
}
```

> **Source:** [sdk `IComIID.cs` (Framework)][sdk-icom-fw]

**Partial to implement `IComIID` on each COM struct (Framework only):**

```csharp
// In Framework/ folder, only compiled for net472
namespace Windows.Win32.System.Com.Urlmon;

internal partial struct IInternetSecurityManager : IComIID
{
    readonly Guid IComIID.Guid => IID_Guid;
}
```

> **Source:** [sdk `IInternetSecurityManager.cs`][sdk-ism-partial]

**`IID.Get<T>()` — works on both targets:**

```csharp
internal static class IID
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid Get<T>() where T : unmanaged, IComIID
    {
#if NETFRAMEWORK
        // Instance method on Framework
        return default(T).Guid;
#else
        // Static abstract on modern .NET
        return T.Guid;
#endif
    }
}
```

> **Source:** [sdk `IID.cs`][sdk-iid]

### 4.5 `HRESULT` Handling

CsWin32 generates an `HRESULT` struct with helper properties. Add a partial for common patterns:

```csharp
internal readonly partial struct HRESULT
{
    public bool Failed => Value < 0;
    public bool Succeeded => Value >= 0;

    public void ThrowOnFailure()
    {
        if (Failed) Marshal.ThrowExceptionForHR(Value);
    }
}
```

Because `preserveSigMethods: ["*"]` is set, all COM methods return `HRESULT` directly:

```csharp
HRESULT hr = securityManager.Pointer->MapUrlToZone(filename, out uint zone, PInvoke.MUTZ_ISFILE);
if (hr.Failed) { return false; }
```

### 4.6 Replacing `[DllImport]` with Generated `PInvoke` Calls

**Before:**

```csharp
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
internal static extern SafeWaitHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

[DllImport("kernel32.dll", SetLastError = true)]
internal static extern bool SetInformationJobObject(IntPtr hJob, ...);

[DllImport("kernel32.dll", SetLastError = true)]
internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
```

**After:**

Simply list the function in `NativeMethods.txt` and call `PInvoke.<FunctionName>()`:

```csharp
HANDLE job = PInvoke.CreateJobObject(null, null);
if (job.IsNull) return null;

PInvoke.SetInformationJobObject(
    job,
    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
    &information,
    (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

PInvoke.AssignProcessToJobObject(job, process);
```

> **Source:** [sdk `ProcessReaper.cs`][sdk-processreaper]

### 4.7 P/Invoke on Unix: `LibraryImport` for .NET, `DllImport` for Framework

For non-Windows P/Invoke that must work on both targets:

```csharp
internal static partial class NativeMethods
{
#if NET
    internal static partial class Posix
    {
        [LibraryImport("libc", SetLastError = true)]
        internal static partial int kill(int pid, int sig);

        internal const int SIGINT = 2;
        internal const int SIGTERM = 15;
    }
#endif
}
```

On .NET Framework, `LibraryImport` is not available; use `DllImport` behind `#if NETFRAMEWORK`:

```csharp
#if NETFRAMEWORK
    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int sig);
#endif
```

> **Source:** [sdk `NativeMethods.cs`][sdk-nmcs] · [sdk PR#52822][sdk-pr52822]

## 5. Step-by-Step Migration Checklist

1. **Add `NativeMethods.json`** with `allowMarshaling: false`, `useSafeHandles: false`, `preserveSigMethods: ["*"]`
2. **Add `NativeMethods.txt`** — list every function, interface, struct, constant, and enum you use
3. **Add CsWin32 PackageReference** with `PrivateAssets="all"`
4. **Add PolySharp** (if multi-targeting .NET Framework)
5. **Delete hand-written `[ComImport]` interfaces** — CsWin32 generates struct-based COM wrappers instead
6. **Delete hand-written `[DllImport]` declarations** — use `PInvoke.<Function>()` calls
7. **Delete hand-written structs/enums** for Win32 types — CsWin32 generates them from metadata
8. **Add `IComIID` interface** for .NET Framework compatibility (instance-based, not `static abstract`)
9. **Add partial struct implementations** for COM interfaces on .NET Framework that implement `IComIID`
10. **Add `IID.Get<T>()` helper** with `#if NETFRAMEWORK` / `#else` branching
11. **Add `ComScope<T>` ref struct** for COM pointer lifetime management
12. **Add `ComClassFactory`** to replace `Activator.CreateInstance` for COM object creation
13. **Add `BSTR` partial** with `IDisposable` for COM string management
14. **Replace `Marshal.ThrowExceptionForHR()`** with `HRESULT.ThrowOnFailure()` patterns
15. **Replace `Marshal.StructureToPtr` / `Marshal.AllocHGlobal`** with direct `unsafe` struct + `sizeof()` patterns
16. **Add source-build stubs** for Windows-only functionality when building on Linux/macOS
17. **Suppress CA1416** if the project is not Windows-only: `<NoWarn>$(NoWarn);CA1416</NoWarn>`
18. **Build and verify** — CsWin32 generates code at compile-time; check for missing types

## 6. Common Gotchas

### CsWin32 Only Generates for .NET (Not .NET Framework)

CsWin32 generates struct-based COM wrappers that use `static abstract` interface members (via `IComIID`). This feature requires .NET 7+. On .NET Framework:

- CsWin32 **still generates** the P/Invoke methods and structs
- But `IComIID` uses `static abstract`, which doesn't compile on Framework
- **Solution:** Provide your own `IComIID` interface (instance-based) and partial implementations per COM struct

### `allowMarshaling: false` Means No RCW / `[MarshalAs]`

With `allowMarshaling: false`, CsWin32 generates raw `struct*` pointer types, not `[ComImport]` RCW interfaces. This means:

- No automatic reference counting — you must call `Release()` (use `ComScope<T>`)
- No `Marshal.GetComInterfaceForObject()` — use `QueryInterface` directly
- Parameters are `T*` pointers, not managed interfaces

### `preserveSigMethods: ["*"]` Changes Error Handling

All COM methods return `HRESULT` instead of throwing `COMException`. You must check each return:

```csharp
// Old: throws COMException on failure
internetSecurityManager.MapUrlToZone(url, out zone, 0);

// New: returns HRESULT
HRESULT hr = securityManager.Pointer->MapUrlToZone(url, out zone, PInvoke.MUTZ_ISFILE);
if (hr.Failed) { /* handle error */ }
```

### `BSTR` Requires Explicit Disposal

Unlike `[MarshalAs(UnmanagedType.BStr)]` which is automatically marshalled, CsWin32's `BSTR` struct needs explicit `Dispose()`:

```csharp
using BSTR str = new("value");  // Allocates via SysAllocString
// ... use str ...
// Dispose() calls SysFreeString
```

### WDK Functions Need Separate Configuration

Functions from the Windows Driver Kit (like `NtQueryInformationProcess`) come from the `Microsoft.Windows.WDK.Win32Metadata` package. Add the WDK CsWin32 package alongside the standard one:

```csharp
// Usage with WDK
WDK.PInvoke.NtQueryInformationProcess(
    (HANDLE)handle.DangerousGetHandle(),
    PROCESSINFOCLASS.ProcessBasicInformation,
    &info,
    (uint)sizeof(PROCESS_BASIC_INFORMATION),
    ref returnLength);
```

> **Source:** [sdk `ProcessExtensions.cs`][sdk-procext]

## 7. Directory Structure

Follow the dotnet/sdk pattern for organizing CsWin32 helper code:

```
ProjectRoot/
├── NativeMethods.json          # CsWin32 configuration
├── NativeMethods.txt           # List of APIs to generate
├── GlobalUsings.cs             # Common using directives
├── Framework/                  # .NET Framework-only polyfills
│   ├── Windows/Win32/
│   │   └── IComIID.cs          # Instance-based IComIID
│   └── System/Com/Urlmon/
│       └── IInternetSecurityManager.cs  # IComIID partial
└── Windows/Win32/              # Shared helpers (both targets)
    ├── Foundation/
    │   ├── BSTR.cs             # BSTR partial with IDisposable
    │   ├── HRESULT.cs          # HRESULT partial with helpers
    │   ├── IID.cs              # IID.Get<T>() helper
    │   └── PWSTR.cs            # PWSTR extensions
    └── System/Com/
        ├── CLSID.cs            # Class ID constants
        ├── ComClassFactory.cs  # COM activation wrapper
        └── ComScope{T}.cs     # COM pointer lifetime scope
```

## Links to Source Files

[sdk-csproj]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Microsoft.DotNet.Cli.Utils.csproj
[sdk-nm-json]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/NativeMethods.json
[sdk-nm-txt]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/NativeMethods.txt
[sdk-comscope]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Windows/Win32/System/Com/ComScope%7BT%7D.cs
[sdk-comfactory]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Windows/Win32/System/Com/ComClassFactory.cs
[sdk-bstr]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Windows/Win32/Foundation/BSTR.cs
[sdk-iid]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Windows/Win32/Foundation/IID.cs
[sdk-icom-fw]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Framework/Windows/Win32/IComIID.cs
[sdk-ism-partial]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Framework/System/Com/UrlMon/IInternetSecurityManager.cs
[sdk-dfd]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/DangerousFileDetector.cs
[sdk-processreaper]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/ProcessReaper.cs
[sdk-procext]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/Extensions/ProcessExtensions.cs
[sdk-nmcs]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/NativeMethods.cs
[sdk-globalusings]: https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.Utils/GlobalUsings.cs
[sdk-pr52822]: https://github.com/dotnet/sdk/pull/52822
[wf-nm-json]: https://github.com/dotnet/winforms/blob/main/src/System.Private.Windows.Core/src/NativeMethods.json
[wf-nm-txt]: https://github.com/dotnet/winforms/blob/main/src/System.Private.Windows.Core/src/NativeMethods.txt
