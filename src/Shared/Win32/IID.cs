// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Copied from dotnet/sdk (src/Cli/Microsoft.DotNet.Cli.Utils/Windows/Win32/Foundation/IID.cs)
// to provide IID lookup for CsWin32 struct-based COM interfaces.

using System;
using System.Runtime.CompilerServices;
using Windows.Win32;

namespace Microsoft.Build.Shared.Win32;

internal static unsafe class IID
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid Get<T>() where T : unmanaged, IComIID
    {
#if NETFRAMEWORK
        // On .NET Framework, IComIID is instance-based (no static abstract support).
        return default(T).Guid;
#else
        // On .NET 7+, CsWin32 generates IComIID with static abstract Guid property.
        return T.Guid;
#endif
    }
}

