// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Partial extension for CsWin32-generated BSTR struct to add IDisposable
// and safe construction from managed strings.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Windows.Win32.Foundation;

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

