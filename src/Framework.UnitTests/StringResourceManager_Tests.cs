// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
#if NET
using System.Runtime.Loader;
#else
using System.Runtime.Serialization.Formatters.Binary;
#endif
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework.Resources;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public class StringResourceManagerTests(ITestOutputHelper output)
{
    private const string BaseName = "StringResourceTests";
    private readonly ITestOutputHelper _output = output;
    private static readonly string[] s_keys =
        ["Greeting", "NeutralOnly", "Spacing", "Empty", "Dotted.Key", "NumericFormat", "Braces"];

    private static StringResourceManager CreateManager(string baseName = BaseName)
        => new(baseName, typeof(StringResourceManagerTests).Assembly, s_keys.Length, GetResourceIndex);

    private static int GetResourceIndex(string name) => Array.IndexOf(s_keys, name);

    [Fact]
    public void StaticResourceUsesItsSlotWithoutDynamicKeyDispatch()
    {
        StringResourceManager manager = new(BaseName, typeof(StringResourceManagerTests).Assembly, s_keys.Length,
            static _ => throw new InvalidOperationException("Dynamic resource dispatch must not run."));
        int providerCalls = 0;
        StringResource resource = new("Spacing", 2, () =>
        {
            providerCalls++;
            return manager;
        });
        providerCalls.ShouldBe(0);
        string value = resource.GetString(manager, CultureInfo.InvariantCulture)!;
        value.ShouldBe("  leading and trailing\n ");
        resource.GetString(manager, CultureInfo.InvariantCulture).ShouldBeSameAs(value);
        providerCalls.ShouldBe(2);
        manager.ReleaseAllResources();
    }

    [Fact]
    public void StaticResourceDoesNotReuseAnotherManagersSlot()
    {
        StringResourceManager owner = CreateManager("SharedStringResourceTests");
        StringResourceManager primary = new(BaseName, typeof(StringResourceManagerTests).Assembly, s_keys.Length,
            static _ => throw new InvalidOperationException("Dynamic resource dispatch must not run."));
        StringResource resource = new("NeutralOnly", 0, () => owner);
        resource.GetString(primary, CultureInfo.GetCultureInfo("de-DE")).ShouldBe("neutral");
        resource.GetString(owner, CultureInfo.GetCultureInfo("de-DE")).ShouldBe("shared localized");
        primary.ReleaseAllResources();
        owner.ReleaseAllResources();
    }

    [Fact]
    public void StaticResourcePreservesCustomResourceManagers()
    {
        ResourceManager custom = new(BaseName, typeof(StringResourceManagerTests).Assembly);
        StringResource resource = new("Greeting", 0,
            static () => throw new InvalidOperationException("Custom managers must not initialize the generated provider."));
        resource.GetString(custom, CultureInfo.GetCultureInfo("de-DE")).ShouldBe("Guten Tag");
        custom.ReleaseAllResources();
    }

    [Fact]
    public void PrimaryNeutralTextWinsBeforeSharedLocalizedFallback()
    {
        StringResourceManager primary = CreateManager();
        StringResourceManager shared = CreateManager("SharedStringResourceTests");
        CultureInfo culture = CultureInfo.GetCultureInfo("de-DE");
        shared.GetString("NeutralOnly", culture).ShouldBe("shared localized");
        (primary.GetString("NeutralOnly", culture) ?? shared.GetString("NeutralOnly", culture)).ShouldBe("neutral");
        (primary.GetString("SharedOnly", culture) ?? shared.GetString("SharedOnly", culture)).ShouldBe("shared localized only");
        primary.ReleaseAllResources();
        shared.ReleaseAllResources();
    }

    [Theory]
    [InlineData("")]
    [InlineData("en-US")]
    [InlineData("de")]
    [InlineData("de-DE")]
    [InlineData("de-AT")]
    [InlineData("fr-FR")]
    public void LookupMatchesPlatformForEveryKeyAndCulture(string cultureName)
    {
        ResourceManager oracle = new(BaseName, typeof(StringResourceManagerTests).Assembly);
        StringResourceManager candidate = CreateManager();
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (string key in s_keys)
        {
            for (int repetition = 0; repetition < 3; repetition++)
            {
                candidate.GetString(key, culture).ShouldBe(oracle.GetString(key, culture));
            }
        }

        candidate.GetString("ParentOnly", culture).ShouldBe(oracle.GetString("ParentOnly", culture));
        candidate.GetString("ChildOnly", culture).ShouldBe(oracle.GetString("ChildOnly", culture));
        candidate.GetString("missing", culture).ShouldBeNull();
        candidate.GetString("greeting", culture).ShouldBeNull();
        candidate.GetString("Spacing", CultureInfo.InvariantCulture).ShouldBe("  leading and trailing\n ");
        candidate.ReleaseAllResources();
        oracle.ReleaseAllResources();
    }

    [Fact]
    public void RepeatedLookupsReuseDecodedStringsAndReleaseInvalidatesSlots()
    {
        StringResourceManager manager = CreateManager();
        string first = manager.GetString("Spacing", CultureInfo.InvariantCulture)!;
        manager.GetString("Spacing", CultureInfo.InvariantCulture).ShouldBeSameAs(first);
        manager.GetString(GetResourceIndex("Spacing"), "Spacing", CultureInfo.InvariantCulture).ShouldBeSameAs(first);
        manager.ReleaseAllResources();
        string next = manager.GetString("Spacing", CultureInfo.InvariantCulture)!;
        next.ShouldBe(first);
        next.ShouldNotBeSameAs(first);
        manager.ReleaseAllResources();
    }

    [Fact]
    public void CaseInsensitiveAndResourceSetApisRemainPlatformCompatible()
    {
        StringResourceManager manager = CreateManager();
        manager.BaseName.ShouldBe(BaseName);
        manager.GetString("greeting", CultureInfo.InvariantCulture).ShouldBeNull();
        manager.IgnoreCase = true;
        manager.GetString("greeting", CultureInfo.InvariantCulture).ShouldBe("Hello");
        manager.GetObject("Greeting", CultureInfo.InvariantCulture).ShouldBe("Hello");
        manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!
            .GetString("Spacing").ShouldBe(manager.GetString("Spacing", CultureInfo.InvariantCulture));
        manager.IgnoreCase = false;
        manager.GetString("greeting", CultureInfo.InvariantCulture).ShouldBeNull();
        manager.ReleaseAllResources();
    }

    [Fact]
    public void MissingCatalogDoesNotBecomeAMissingKey()
    {
        StringResourceManager manager = CreateManager("MissingCatalog");
        Should.Throw<MissingManifestResourceException>(() => manager.GetString("unknown", CultureInfo.InvariantCulture));
        Should.Throw<ArgumentNullException>(() => manager.GetString(null!, CultureInfo.InvariantCulture));
        manager.ReleaseAllResources();
    }

    [Fact]
    public async Task ConcurrentImplicitCulturesDoNotShareTranslatedValues()
    {
        StringResourceManager manager = CreateManager();
        Task[] tasks = new Task[12];
        for (int i = 0; i < tasks.Length; i++)
        {
            string cultureName = i % 2 == 0 ? "de-DE" : "fr-FR";
            string expected = i % 2 == 0 ? "Guten Tag" : "Hello";
            tasks[i] = Task.Run(() =>
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                for (int lookup = 0; lookup < 100; lookup++)
                {
                    manager.GetString("Greeting").ShouldBe(expected);
                }
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);
        manager.ReleaseAllResources();
    }

    [Fact]
    public async Task ImplicitCultureChangesAfterFirstAccessAreObserved()
    {
        StringResourceManager manager = CreateManager();
        await Task.Run(() =>
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            manager.GetString("Greeting").ShouldBe("Hallo");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            manager.GetString("Greeting").ShouldBe("Guten Tag");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            manager.GetString("Greeting").ShouldBe("Hello");
            manager.GetString("Greeting", CultureInfo.GetCultureInfo("de")).ShouldBe("Hallo");
        }, TestContext.Current.CancellationToken);
        manager.ReleaseAllResources();
    }

    [Fact]
    public async Task ReleaseCannotDisposeBackingUnderAnActiveLookup()
    {
        StringResourceManager manager = CreateManager();
        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                manager.GetString("Greeting", CultureInfo.GetCultureInfo("de-DE")).ShouldBe("Guten Tag");
                manager.GetString("NeutralOnly", CultureInfo.InvariantCulture).ShouldBe("neutral");
            }
        }, TestContext.Current.CancellationToken);
        Task releaser = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                manager.ReleaseAllResources();
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(reader, releaser);
        manager.ReleaseAllResources();
    }

    [Fact]
    public void SharedFrameworkCatalogMatchesPlatformAcrossShippedCultures()
    {
        ResourceManager oracle = new("Microsoft.Build.Framework.Resources.SR", typeof(SR).Assembly);
        using ResourceSet neutral = oracle.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;
        int keys = 0;
        foreach (DictionaryEntry entry in neutral)
        {
            string key = (string)entry.Key;
            keys++;
            foreach (string cultureName in (string[])["", "en", "de-DE", "fr-FR", "ja-JP", "zh-CN", "tr-TR"])
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
                SR.ResourceManager.GetString(key, culture).ShouldBe(oracle.GetString(key, culture));
            }
        }

        keys.ShouldBeGreaterThan(0);
        _output.WriteLine($"Compared {keys} shared catalog keys.");
    }

    [Fact]
    public void GeneratedAccessorsUseExactKeysTextAndCultureSnapshots()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.WithTransientTestState(new ResourceCultureState());
        GeneratedResourceCatalog.Culture = CultureInfo.GetCultureInfo("de");
        GeneratedResourceCatalog.Greeting.ShouldBe("Hallo");
        GeneratedResourceCatalog.Culture = CultureInfo.GetCultureInfo("de-DE");
        GeneratedResourceCatalog.Greeting.ShouldBe("Guten Tag");
        GeneratedResourceCatalog.Culture = CultureInfo.InvariantCulture;
        GeneratedResourceCatalog.Dotted_Key.ShouldBe("dotted");
        GeneratedResourceCatalog.Spacing.ShouldBe("  leading and trailing\n ");
        GeneratedResourceCatalog.Braces.ShouldBe("{{literal}} {not a format}");
        GeneratedResourceCatalog.FormatNumericFormat(1234.5).ShouldBe(
            string.Format(CultureInfo.InvariantCulture, "number: {0:N2}", 1234.5));
    }

    private sealed class ResourceCultureState : TransientTestState
    {
        private readonly CultureInfo? _previous = GeneratedResourceCatalog.Culture;
        public override void Revert() => GeneratedResourceCatalog.Culture = _previous;
    }

#if NET
    [Fact]
    public void AValidChildStringDoesNotLoadAnUnusedMalformedParentTable()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string assemblyName = typeof(StringResourceManagerTests).Assembly.GetName().Name!;
        string ownerPath = Path.Combine(directory, assemblyName + ".dll");
        File.Copy(typeof(StringResourceManagerTests).Assembly.Location, ownerPath);
        foreach (string culture in (string[])["de", "de-DE"])
        {
            string target = Path.Combine(directory, culture);
            Directory.CreateDirectory(target);
            string destination = Path.Combine(target, assemblyName + ".resources.dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, culture, assemblyName + ".resources.dll"), destination);
            if (culture == "de")
            {
                byte[] image = File.ReadAllBytes(destination);
                int found = -1;
                for (int i = 0; i <= image.Length - 4; i++)
                {
                    if (image[i] == 0xCE && image[i + 1] == 0xCA && image[i + 2] == 0xEF && image[i + 3] == 0xBE)
                    {
                        found = i;
                        break;
                    }
                }

                found.ShouldBeGreaterThanOrEqualTo(0);
                image[found] = 0;
                File.WriteAllBytes(destination, image);
            }
        }

        AssemblyLoadContext context = new("StringResourceFaultFixture", isCollectible: true);
        env.WithTransientTestState(new LoadContextState(context));
        System.Reflection.Assembly owner = context.LoadFromAssemblyPath(ownerPath);
        ResourceManager oracle = new(BaseName, owner);
        StringResourceManager candidate = new(BaseName, owner, s_keys.Length, GetResourceIndex);
        CultureInfo requested = CultureInfo.GetCultureInfo("de-DE");
        oracle.GetString("Greeting", requested).ShouldBe("Guten Tag");
        candidate.GetString("Greeting", requested).ShouldBe("Guten Tag");
        Exception? expected = Record.Exception(() => oracle.GetString("ParentOnly", requested));
        Exception? actual = Record.Exception(() => candidate.GetString("ParentOnly", requested));
        expected.ShouldNotBeNull();
        actual.ShouldNotBeNull();
        actual.GetType().ShouldBe(expected.GetType());
        candidate.ReleaseAllResources();
        oracle.ReleaseAllResources();
    }

    private sealed class LoadContextState(AssemblyLoadContext context) : TransientTestState
    {
        public override void Revert() => context.Unload();
    }
#else
    [Fact]
    public void ResourceManagerCanBeSerializedAcrossFrameworkAppDomainBoundaries()
    {
        StringResourceManager manager = CreateManager();
        manager.GetString("Greeting", CultureInfo.GetCultureInfo("de")).ShouldBe("Hallo");
        using MemoryStream stream = new();
        BinaryFormatter formatter = new();
        formatter.Serialize(stream, manager);
        stream.Position = 0;
        StringResourceManager restored = (StringResourceManager)formatter.Deserialize(stream);
        restored.GetString("Greeting", CultureInfo.GetCultureInfo("de-DE")).ShouldBe("Guten Tag");
        manager.ReleaseAllResources();
        restored.ReleaseAllResources();
    }
#endif
}
