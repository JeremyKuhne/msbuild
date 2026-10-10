// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Microsoft.Build.Framework.Resources;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public class BuiltInStringResourceCatalogTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void AllGeneratedStaticResourceReferencesMatchLocalizedPlatformValues()
    {
        (Assembly Assembly, string BaseName, string Catalog)[] catalogs =
        [
            (typeof(global::Microsoft.Build.Evaluation.Project).Assembly, "Microsoft.Build.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Tasks.Copy).Assembly, "Microsoft.Build.Tasks.Core.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Utilities.Task).Assembly, "Microsoft.Build.Utilities.Core.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.CommandLine.MSBuildApp).Assembly, "MSBuild.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Framework.IBuildEngine).Assembly, "Microsoft.Build.Framework.Resources.SR", "Microsoft.Build.Framework.Resources.SR")
        ];
        int total = 0;
        foreach (var catalog in catalogs)
        {
            Type generated = catalog.Assembly.GetType(catalog.Catalog, throwOnError: true)!;
            Type references = generated.GetNestedType("Resources", BindingFlags.NonPublic)!;
            ResourceManager manager = (ResourceManager)generated.GetProperty("ResourceManager", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            ResourceManager oracle = new(catalog.BaseName, catalog.Assembly);
            int count = 0;
            foreach (PropertyInfo property in references.GetProperties(BindingFlags.Static | BindingFlags.NonPublic))
            {
                StringResource resource = (StringResource)property.GetValue(null)!;
                foreach (string name in (string[])["", "en-US", "de", "de-DE", "fr", "ja", "tr", "zh-Hans"])
                {
                    CultureInfo culture = CultureInfo.GetCultureInfo(name);
                    resource.GetString(manager, culture).ShouldBe(oracle.GetString(resource.Name, culture),
                        $"{catalog.BaseName}: static {property.Name}, culture {name}");
                }

                count++;
            }

            count.ShouldBeGreaterThan(0);
            _output.WriteLine($"{catalog.BaseName}: checked {count} static indexed properties.");
            total += count;
            oracle.ReleaseAllResources();
        }

        total.ShouldBe(1674);
    }

    [Fact]
    public void AllBuiltInCatalogsMatchThePlatformAcrossSupportedLanguages()
    {
        (Assembly Assembly, string BaseName, string Catalog)[] catalogs =
        [
            (typeof(global::Microsoft.Build.Evaluation.Project).Assembly, "Microsoft.Build.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Tasks.Copy).Assembly, "Microsoft.Build.Tasks.Core.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Utilities.Task).Assembly, "Microsoft.Build.Utilities.Core.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.CommandLine.MSBuildApp).Assembly, "MSBuild.Strings", "Microsoft.Build.Shared.PrimaryResourceCatalog"),
            (typeof(global::Microsoft.Build.Framework.IBuildEngine).Assembly, "Microsoft.Build.Framework.Resources.SR", "Microsoft.Build.Framework.Resources.SR")
        ];
        string[] cultures = ["", "en", "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant"];
        int total = 0;
        foreach (var catalog in catalogs)
        {
            Type generated = catalog.Assembly.GetType(catalog.Catalog, throwOnError: true)!;
            ResourceManager candidate = (ResourceManager)generated.GetProperty(
                "ResourceManager", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            if (StringResourceManager.IsEnabled)
            {
                candidate.ShouldBeOfType<StringResourceManager>();
            }

            ResourceManager oracle = new(catalog.BaseName, catalog.Assembly);
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (string name in cultures)
            {
                ResourceSet resources = oracle.GetResourceSet(CultureInfo.GetCultureInfo(name), true, true)!;
                foreach (DictionaryEntry entry in resources)
                {
                    keys.Add((string)entry.Key);
                }
            }

            int count = 0;
            foreach (string key in keys)
            {
                count++;
                foreach (string name in cultures)
                {
                    CultureInfo culture = CultureInfo.GetCultureInfo(name);
                    candidate.GetString(key, culture).ShouldBe(oracle.GetString(key, culture),
                        $"{catalog.BaseName}: {key}, culture {name}");
                }
            }

            count.ShouldBeGreaterThan(0);
            candidate.GetString("___missing_resource___", CultureInfo.InvariantCulture).ShouldBeNull();
            oracle.ReleaseAllResources();
            total += count;
            _output.WriteLine($"{catalog.BaseName}: compared {count} keys across {cultures.Length} culture requests.");
        }

        total.ShouldBeGreaterThan(1600);
    }
}
