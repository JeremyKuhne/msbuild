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
public class StringResourceLookupBenchmark
{
    private const string BaseName = "Microsoft.Build.Framework.Resources.SR";
    private const string Key = "Argument_EmptyString";
    private ResourceManager _platform = null!;
    private StringResourceManager _indexed = null!;
    private CultureInfo _culture = null!;
    private string[] _keys = null!;
    private int _index;

    [Params("en", "de-DE")]
    public string CultureName { get; set; } = "en";

    [GlobalSetup]
    public void Setup()
    {
        _culture = CultureInfo.GetCultureInfo(CultureName);
        _platform = new ResourceManager(BaseName, typeof(SR).Assembly);
        List<string> keys = [];
        foreach (DictionaryEntry entry in _platform.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
        {
            keys.Add((string)entry.Key);
        }

        _keys = [.. keys];
        Func<string, int> getIndex = (Func<string, int>)Delegate.CreateDelegate(
            typeof(Func<string, int>), typeof(SR).GetMethod("GetResourceIndex", BindingFlags.Static | BindingFlags.NonPublic)!);
        _index = getIndex(Key);
        if (_keys.Length == 0 || _index < 0)
        {
            throw new InvalidOperationException("The expected generated resource catalog was not found.");
        }

        _indexed = new StringResourceManager(BaseName, typeof(SR).Assembly, _keys.Length, getIndex);
        foreach (string key in _keys)
        {
            if (_indexed.GetString(key, _culture) != _platform.GetString(key, _culture))
            {
                throw new InvalidOperationException($"Resource mismatch: {key}");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _platform.ReleaseAllResources();
        _indexed.ReleaseAllResources();
    }

    [Benchmark(Baseline = true)]
    public string? Platform() => _platform.GetString(Key, _culture);

    [Benchmark]
    public string? IndexedDynamicKey() => _indexed.GetString(Key, _culture);

    [Benchmark]
    public string? IndexedKnownSlot() => _indexed.GetString(_index, Key, _culture);
}

[MemoryDiagnoser]
public class StringResourceCatalogSweepBenchmark
{
    private ResourceManager _platform = null!;
    private StringResourceManager _indexed = null!;
    private string[] _keys = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string baseName = "Microsoft.Build.Framework.Resources.SR";
        _platform = new ResourceManager(baseName, typeof(SR).Assembly);
        List<string> keys = [];
        foreach (DictionaryEntry entry in _platform.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
        {
            keys.Add((string)entry.Key);
        }

        _keys = [.. keys];
        if (_keys.Length == 0)
        {
            throw new InvalidOperationException("The catalog sweep is empty.");
        }

        Func<string, int> getIndex = (Func<string, int>)Delegate.CreateDelegate(
            typeof(Func<string, int>), typeof(SR).GetMethod("GetResourceIndex", BindingFlags.Static | BindingFlags.NonPublic)!);
        _indexed = new StringResourceManager(baseName, typeof(SR).Assembly, _keys.Length, getIndex);
        foreach (string key in _keys)
        {
            if (_indexed.GetString(key, CultureInfo.InvariantCulture) != _platform.GetString(key, CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"Resource mismatch: {key}");
            }
        }

        Console.WriteLine($"Each measured operation is one sweep of {_keys.Length} distinct keys.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _platform.ReleaseAllResources();
        _indexed.ReleaseAllResources();
    }

    [Benchmark(Baseline = true)]
    public int PlatformSweep() => Sweep(_platform);

    [Benchmark]
    public int IndexedSweep() => Sweep(_indexed);

    private int Sweep(ResourceManager manager)
    {
        int length = 0;
        foreach (string key in _keys)
        {
            length += manager.GetString(key, CultureInfo.InvariantCulture)!.Length;
        }

        return length;
    }
}
