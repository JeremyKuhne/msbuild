// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Partial struct adding IDisposable to CsWin32-generated VARIANT.
// Adapted from dotnet/winforms (src/System.Private.Windows.Core/src/Windows/Win32/System/Variant/VARIANT.cs).

using System;

namespace Windows.Win32.System.Variant;

internal unsafe partial struct VARIANT : IDisposable
{
    /// <summary>
    ///  Clears the <see cref="VARIANT"/> and releases any resources.
    /// </summary>
    /// <remarks>
    ///  <see href="https://learn.microsoft.com/windows/win32/api/oleauto/nf-oleauto-variantclear"/>
    /// </remarks>
    public void Dispose()
    {
        fixed (VARIANT* pThis = &this)
        {
            PInvoke.VariantClear(pThis);
        }
    }
}

