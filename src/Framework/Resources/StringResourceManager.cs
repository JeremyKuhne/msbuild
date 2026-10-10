// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// Adapted from Touki's StringResourceManager and SatelliteStringResourceManager.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
#if NETFRAMEWORK
using System.Runtime.Serialization;
#endif
using System.Threading;

namespace Microsoft.Build.Framework.Resources;

/// <summary>
/// Provides culture-aware indexed lookup behind the existing ResourceManager contract.
/// </summary>
#if NETFRAMEWORK
[Serializable]
#endif
internal sealed class StringResourceManager : ResourceManager
{
    private readonly Assembly _assembly;
    private readonly string _neutralCultureName;
    private readonly int _resourceCount;
    private readonly Func<string, int> _getResourceIndex;
    [NonSerialized]
    private object _gate = new();
    private readonly bool _usePlatformResources;
    [NonSerialized]
    private State _state = new();
    [NonSerialized]
    private CultureCache? _lastCultureCache;
    private Version? _satelliteVersion;
    private bool _satelliteVersionRead;

    internal StringResourceManager(string baseName, Assembly assembly, int resourceCount, Func<string, int> getResourceIndex)
        : base(baseName, assembly)
    {
        _assembly = assembly;
        _resourceCount = resourceCount;
        _getResourceIndex = getResourceIndex;
        _neutralCultureName = GetNeutralResourcesLanguage(assembly).Name;
        _usePlatformResources = FallbackLocation == UltimateResourceFallbackLocation.Satellite;
#if NET
        // ILC can embed localized resources without runtime-bindable satellite assemblies.
        _usePlatformResources |= !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
#endif
    }

    internal static bool IsEnabled => EnvironmentUtilities.IsValueOneOrTrue("MSBUILD_USE_INDEXED_STRING_RESOURCES");

    internal static ResourceManager Create(string baseName, Assembly assembly, int resourceCount, Func<string, int> getResourceIndex)
        => IsEnabled
            ? new StringResourceManager(baseName, assembly, resourceCount, getResourceIndex)
            : new ResourceManager(baseName, assembly);

    public override string? GetString(string name) => GetString(name, culture: null);

    public override string? GetString(string name, CultureInfo? culture)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (IgnoreCase || _usePlatformResources)
        {
            return base.GetString(name, culture);
        }

        return GetString(_getResourceIndex(name), name, culture);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal string? GetString(int index, string name, CultureInfo? culture)
    {
        if (IgnoreCase || _usePlatformResources)
        {
            return base.GetString(name, culture);
        }

        culture ??= CultureInfo.CurrentUICulture;
        CultureCache? cache = Volatile.Read(ref _lastCultureCache);
        if (index >= 0 && cache is not null && cache.CultureName == culture.Name)
        {
            string? value = Volatile.Read(ref cache.Values[index]);
            if (value is not null)
            {
                return value;
            }
        }

        return GetStringSlow(index, name, culture);
    }

    private string? GetStringSlow(int index, string name, CultureInfo culture)
    {
        lock (_gate)
        {
            State state = _state;
            if (!state.Cultures.TryGetValue(culture.Name, out CultureCache? cache))
            {
                cache = new CultureCache(culture.Name, _resourceCount);
                state.Cultures.Add(culture.Name, cache);
            }

            Volatile.Write(ref _lastCultureCache, cache);
            if (index >= 0 && cache.Values[index] is string cached)
            {
                return cached;
            }

            for (CultureInfo current = culture; ; current = current.Parent)
            {
                bool neutral = current.Name.Length == 0 || current.Name == _neutralCultureName;
                string tableCulture = neutral ? string.Empty : current.Name;
                IndexedStringResourceTable? table = GetTable(state, current, tableCulture);
                if (table is not null)
                {
                    IndexedStringResourceTable.LookupResult result = table.Lookup(name, out string? value);
                    if (result == IndexedStringResourceTable.LookupResult.NonString)
                    {
                        // Preserve platform GetString's non-string exception and reader semantics.
                        return base.GetString(name, culture);
                    }

                    if (value is not null)
                    {
                        if (index >= 0)
                        {
                            Volatile.Write(ref cache.Values[index], value);
                        }

                        return value;
                    }
                }

                if (neutral)
                {
                    return null;
                }
            }
        }
    }

    private IndexedStringResourceTable? GetTable(State state, CultureInfo culture, string tableCulture)
    {
        if (state.Tables.TryGetValue(tableCulture, out IndexedStringResourceTable? table))
        {
            return table;
        }

        bool neutral = tableCulture.Length == 0;
        Assembly? owner = neutral ? _assembly : GetSatellite(culture);
        string resourceName = neutral ? BaseName + ".resources" : BaseName + "." + tableCulture + ".resources";
        Stream? stream = owner?.GetManifestResourceStream(resourceName);
        if (stream is null && owner is not null)
        {
            // ResourceManager's manifest lookup is case-insensitive, unlike its string keys.
            foreach (string manifestName in owner.GetManifestResourceNames())
            {
                if (string.Equals(manifestName, resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    if (stream is not null)
                    {
                        stream.Dispose();
                        throw new MissingManifestResourceException("Multiple resource manifests differ only by case.");
                    }

                    stream = owner.GetManifestResourceStream(manifestName);
                }
            }
        }

        if (stream is null)
        {
            if (neutral)
            {
                // Let the platform author the existing missing-manifest diagnostic.
                base.GetString(string.Empty, CultureInfo.InvariantCulture);
                throw new MissingManifestResourceException(resourceName);
            }

            state.Tables.Add(tableCulture, null);
            return null;
        }

        try
        {
            table = new IndexedStringResourceTable(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        // Satellite binding can reenter lookup through host assembly-resolve callbacks.
        if (state.Tables.TryGetValue(tableCulture, out IndexedStringResourceTable? existing))
        {
            table.Dispose();
            return existing;
        }

        state.Tables.Add(tableCulture, table);
        return table;
    }

    private Assembly? GetSatellite(CultureInfo culture)
    {
        if (!_satelliteVersionRead)
        {
            _satelliteVersion = GetSatelliteContractVersion(_assembly);
            _satelliteVersionRead = true;
        }

        try
        {
            return _satelliteVersion is null
                ? _assembly.GetSatelliteAssembly(culture)
                : _assembly.GetSatelliteAssembly(culture, _satelliteVersion);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Match ResourceManager's satellite-bind fallback. Resource-image failures still throw.
            return null;
        }
    }

    public override void ReleaseAllResources()
    {
        lock (_gate)
        {
            State previous = _state;
            _state = new State();
            Volatile.Write(ref _lastCultureCache, null);
            foreach (IndexedStringResourceTable? table in previous.Tables.Values)
            {
                table?.Dispose();
            }

            base.ReleaseAllResources();
        }
    }

#if NETFRAMEWORK
    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        _gate = new object();
        _state = new State();
        _lastCultureCache = null;
    }
#endif

    private sealed class State
    {
        internal Dictionary<string, CultureCache> Cultures { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, IndexedStringResourceTable?> Tables { get; } = new(StringComparer.Ordinal);
    }

    private sealed class CultureCache(string cultureName, int resourceCount)
    {
        internal string CultureName { get; } = cultureName;
        internal string?[] Values { get; } = new string?[resourceCount];
    }
}
