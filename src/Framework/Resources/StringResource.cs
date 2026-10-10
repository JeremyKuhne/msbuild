// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Globalization;
using System.Resources;

namespace Microsoft.Build.Framework.Resources;

internal readonly struct StringResource(string name, int index, Func<ResourceManager> getManager)
{
    internal string Name { get; } = name;
    private readonly int _index = index;
    private readonly Func<ResourceManager> _getManager = getManager;

    internal ResourceManager ResourceManager => _getManager();

    internal string? GetString(ResourceManager manager, CultureInfo? culture)
    {
        if (manager is StringResourceManager indexed)
        {
            return indexed.GetString(ReferenceEquals(manager, ResourceManager) ? _index : -1, Name, culture);
        }

        return manager.GetString(Name, culture);
    }
}
