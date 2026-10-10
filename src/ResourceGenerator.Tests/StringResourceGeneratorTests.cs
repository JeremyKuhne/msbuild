// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.Build.ResourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ResourceGenerator.Tests;

public class StringResourceGeneratorTests
{
    [Fact]
    public void UsesLogicalIdentityAndExactKeysWithoutEmbeddingFallbackText()
    {
        string text = """
            <root>
              <data name="Dotted.Key" xml:space="preserve"><value>  trailing  </value></data>
              <data name="class"><value>{raw braces}</value></data>
            </root>
            """;
        GeneratorDriverRunResult result = Run(text);
        result.Diagnostics.ShouldBeEmpty();
        string source = result.GeneratedTrees.Single().GetText().ToString();
        source.ShouldContain("\"Explicit.Catalog\"");
        source.ShouldContain("\"Dotted.Key\"");
        source.ShouldContain("@Dotted_Key");
        source.ShouldContain("@class");
        source.ShouldNotContain("trailing");
        source.ShouldNotContain("raw braces");
    }

    [Theory]
    [InlineData("<root><data name=\"A.B\"><value>x</value></data><data name=\"A_B\"><value>y</value></data></root>")]
    [InlineData("<root><data name=\"Greeting\"><value>x</value></data><data name=\"Greeting\"><value>y</value></data></root>")]
    [InlineData("<root><data name=\"ResourceManager\"><value>x</value></data></root>")]
    [InlineData("<root><data name=\"Resources\"><value>x</value></data></root>")]
    [InlineData("<root><data name=\"LookupHolder\"><value>x</value></data></root>")]
    [InlineData("<root><data name=\"Count\" type=\"System.Int32\"><value>1</value></data></root>")]
    [InlineData("<root>")]
    public void InvalidOrCollidingCatalogIsReportedExplicitly(string text)
    {
        GeneratorDriverRunResult result = Run(text);
        result.Diagnostics.ShouldHaveSingleItem().Id.ShouldBe("MSBRES001");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Fact]
    public void LeavesRawTemplatesUnvalidatedAndEmitsFrameworkFormatOverloads()
    {
        GeneratorDriverRunResult result = Run("""
            <root>
              <data name="Raw"><value>{not a format}</value></data>
              <data name="Formatted"><value>{0:N2} {1}</value></data>
            </root>
            """, formatMethods: true);
        result.Diagnostics.ShouldBeEmpty();
        string source = result.GeneratedTrees.Single().GetText().ToString();
        source.ShouldContain("FormatFormatted(object? p0, object? p1)");
        source.ShouldContain("global::System.String.Format(culture, GetResourceString(0, \"Formatted\", culture), p0, p1)");
        source.ShouldNotContain("ResourceManager.GetString(\"Formatted\"");
        source.ShouldNotContain("FormatRaw");
    }

    [Fact]
    public void StaticResourceReferencesCarryExactKeysAndConstantSlotsLazily()
    {
        GeneratorDriverRunResult result = Run("""
            <root>
              <data name="B"><value>second</value></data>
              <data name="A.Dotted"><value>first</value></data>
            </root>
            """);
        result.Diagnostics.ShouldBeEmpty();
        string source = result.GeneratedTrees.Single().GetText().ToString();
        source.ShouldContain("internal static class Resources");
        source.ShouldContain("@A_Dotted => new(\"A.Dotted\", 0, LookupHolder.GetManager)");
        source.ShouldContain("@B => new(\"B\", 1, LookupHolder.GetManager)");
        source.ShouldContain("static () => ResourceManager");
        source.ShouldContain("@A_Dotted => GetResourceString(0, \"A.Dotted\", Culture)");
    }

    [Fact]
    public void DeterministicOutputDoesNotDependOnResxEntryOrder()
    {
        string first = Run("<root><data name=\"B\"><value>b</value></data><data name=\"A\"><value>a</value></data></root>")
            .GeneratedTrees.Single().GetText().ToString();
        string second = Run("<root><data name=\"A\"><value>a</value></data><data name=\"B\"><value>b</value></data></root>")
            .GeneratedTrees.Single().GetText().ToString();
        first.ShouldBe(second);
    }

    [Fact]
    public void GeneratedSourceCompilesAgainstRuntimeFacadeContract()
    {
        const string contract = """
            namespace Microsoft.Build.Framework.Resources
            {
                internal readonly struct StringResource(string name, int index, System.Func<System.Resources.ResourceManager> getManager)
                {
                    internal string Name => name;
                    internal int Index => index;
                    internal System.Resources.ResourceManager Manager => getManager();
                }

                internal class StringResourceManager : System.Resources.ResourceManager
                {
                    internal static System.Resources.ResourceManager Create(
                        string name, System.Reflection.Assembly assembly, int count, System.Func<string, int> index)
                        => new System.Resources.ResourceManager(name, assembly);
                    internal string? GetString(int index, string key, System.Globalization.CultureInfo? culture)
                        => GetString(key, culture);
                }
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("GeneratorContract",
            [CSharpSyntaxTree.ParseText(contract)],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = Driver("<root><data name=\"Format\"><value>{0} {1} {2}</value></data></root>", true);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out ImmutableArray<Diagnostic> diagnostics);
        diagnostics.ShouldBeEmpty();
        generated.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    private static GeneratorDriverRunResult Run(string text, bool formatMethods = false)
        => Driver(text, formatMethods).RunGenerators(CSharpCompilation.Create("Catalog")).GetRunResult();

    private static GeneratorDriver Driver(string text, bool formatMethods)
        => CSharpGeneratorDriver.Create(
            [new StringResourceGenerator().AsSourceGenerator()],
            [new ResxText(text)], optionsProvider: new OptionsProvider(formatMethods));

    private static IEnumerable<MetadataReference> References()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));

    private sealed class ResxText(string text) : AdditionalText
    {
        public override string Path => "Strings.resx";
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class OptionsProvider(bool formats) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _options = new(formats);
        public override AnalyzerConfigOptions GlobalOptions => _options;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override AnalyzerConfigOptions GetOptions(AdditionalText text) => _options;
    }

    private sealed class Options(bool formats) : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase)
        {
            ["build_metadata.AdditionalFiles.StringResourceClass"] = "Test.Catalog",
            ["build_metadata.AdditionalFiles.LogicalName"] = "Explicit.Catalog.resources",
            ["build_metadata.AdditionalFiles.StringResourceFormatMethods"] = formats ? "true" : "false"
        };

        public override bool TryGetValue(string key, out string value) => _options.TryGetValue(key, out value!);
    }
}
