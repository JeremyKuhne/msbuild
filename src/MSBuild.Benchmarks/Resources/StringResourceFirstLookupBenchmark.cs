// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using BenchmarkDotNet.Attributes;
using Microsoft.Build.Framework.Resources;

namespace MSBuild.Benchmarks;

[MemoryDiagnoser]
public class StringResourceFirstLookupBenchmark
{
    private const string Key = "Argument_EmptyString";
    private ResourceManager _platform = null!;
    private StringResourceManager _indexed = null!;
    private Func<string, int> _getIndex = null!;
    private CultureInfo _culture = null!;
    private int _resourceCount;

    [Params("en", "de-DE")]
    public string CultureName { get; set; } = "en";

    [GlobalSetup]
    public void Setup()
    {
        _culture = CultureInfo.GetCultureInfo(CultureName);
        _getIndex = (Func<string, int>)Delegate.CreateDelegate(
            typeof(Func<string, int>), typeof(SR).GetMethod("GetResourceIndex", BindingFlags.Static | BindingFlags.NonPublic)!);
        ResourceManager catalog = new("Microsoft.Build.Framework.Resources.SR", typeof(SR).Assembly);
        foreach (DictionaryEntry entry in catalog.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
        {
            _resourceCount++;
        }

        catalog.ReleaseAllResources();
        if (_resourceCount == 0)
        {
            throw new InvalidOperationException("The generated catalog is empty.");
        }
    }

    [IterationSetup]
    public void NewManagers()
    {
        const string baseName = "Microsoft.Build.Framework.Resources.SR";
        _platform = new ResourceManager(baseName, typeof(SR).Assembly);
        _indexed = new StringResourceManager(baseName, typeof(SR).Assembly, _resourceCount, _getIndex);
    }

    [IterationCleanup]
    public void Release()
    {
        _platform.ReleaseAllResources();
        _indexed.ReleaseAllResources();
    }

    [Benchmark(Baseline = true)]
    public string? PlatformFirstLookup() => _platform.GetString(Key, _culture);

    [Benchmark]
    public string? IndexedFirstLookup() => _indexed.GetString(Key, _culture);
}
