// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// Adapted from Touki's incremental RESX generator and CSharpResourceRenderer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.Build.ResourceGenerator;

/// <summary>
/// Generates internal cached string catalogs from explicitly opted-in RESX inputs.
/// </summary>
[Generator]
public sealed class StringResourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor s_invalidCatalog = new(
        "MSBRES001", "Invalid internal string resource catalog",
        "Cannot generate string resources for '{0}': {1}. Correct the catalog or its StringResourceClass/LogicalName metadata.",
        "ResourceGeneration", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadInput(pair.Left, pair.Right, cancellationToken))
            .Where(static input => input is not null);
        context.RegisterSourceOutput(inputs, static (production, input) => Generate(production, input!));
    }

    private static Input? ReadInput(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        AnalyzerConfigOptions metadata = options.GetOptions(text);
        if (!metadata.TryGetValue("build_metadata.AdditionalFiles.StringResourceClass", out string? className) ||
            string.IsNullOrEmpty(className))
        {
            return null;
        }

        metadata.TryGetValue("build_metadata.AdditionalFiles.LogicalName", out string? logicalName);
        metadata.TryGetValue("build_metadata.AdditionalFiles.StringResourceFormatMethods", out string? formats);
        return new Input(text.Path, text.GetText(cancellationToken), className, logicalName,
            string.Equals(formats, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static void Generate(SourceProductionContext context, Input input)
    {
        if (input.Text is null)
        {
            Report(context, input, "the compiler did not supply the RESX contents");
            return;
        }

        int separator = input.ClassName.LastIndexOf('.');
        if (separator <= 0 || !ValidQualifiedName(input.ClassName) ||
            input.LogicalName is null || !input.LogicalName.EndsWith(".resources", StringComparison.Ordinal))
        {
            Report(context, input, "a qualified StringResourceClass and a LogicalName ending in .resources are required");
            return;
        }

        XDocument document;
        try
        {
            using StringReader reader = new(input.Text.ToString());
            using XmlReader xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            Report(context, input, exception.Message);
            return;
        }

        if (document.Root is null)
        {
            Report(context, input, "the RESX has no root element");
            return;
        }

        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal)
        {
            "ResourceManager", "Culture", "GetResourceString", "GetResourceIndex", "ManagerHolder", "Resources", "LookupHolder"
        };
        foreach (XElement data in document.Root.Elements("data"))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string? name = data.Attribute("name")?.Value;
            XElement? value = data.Element("value");
            if (name is null || name.Length == 0 || value is null || !names.Add(name))
            {
                Report(context, input, "every resource must have a unique nonempty name and a value");
                return;
            }

            if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null)
            {
                Report(context, input, $"resource '{name}' is not an untyped string");
                return;
            }

            string member = Identifier(name);
            int arguments = input.FormatMethods ? ArgumentCount(value.Value) : 0;
            if (!members.Add(member) || (arguments > 0 && !members.Add("Format" + member)))
            {
                Report(context, input, $"resource '{name}' collides with another generated member");
                return;
            }

            // Values are not trimmed or embedded as fallback literals.
            entries.Add(new Entry(name, member, arguments));
        }

        if (entries.Count == 0 || entries.Count > 4096)
        {
            Report(context, input, "the catalog must contain between 1 and 4096 strings");
            return;
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        string namespaceName = input.ClassName.Substring(0, separator);
        string className = input.ClassName.Substring(separator + 1);
        string baseName = input.LogicalName.Substring(0, input.LogicalName.Length - ".resources".Length);
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.Append("namespace ").Append(namespaceName).AppendLine(";");
        source.Append("internal static partial class @").Append(className).AppendLine();
        source.AppendLine("{");
        source.AppendLine("    private static class ManagerHolder");
        source.AppendLine("    {");
        source.AppendLine("        internal static readonly global::System.Resources.ResourceManager Instance =");
        source.Append("            global::Microsoft.Build.Framework.Resources.StringResourceManager.Create(")
            .Append(Literal(baseName)).Append(", typeof(@").Append(className).Append(").Assembly, ")
            .Append(entries.Count).AppendLine(", GetResourceIndex);");
        source.AppendLine("    }");
        source.AppendLine("    internal static global::System.Resources.ResourceManager ResourceManager => ManagerHolder.Instance;");
        source.AppendLine("    internal static global::System.Globalization.CultureInfo? Culture { get; set; }");
        source.AppendLine("    internal static string GetResourceString(string resourceKey, string? defaultValue = null)");
        source.AppendLine("        => ResourceManager.GetString(resourceKey, Culture)!;");
        source.AppendLine("    private static string GetResourceString(int index, string key, global::System.Globalization.CultureInfo? culture)");
        source.AppendLine("    {");
        source.AppendLine("        var manager = ResourceManager;");
        source.AppendLine("        return manager is global::Microsoft.Build.Framework.Resources.StringResourceManager indexed");
        source.AppendLine("            ? indexed.GetString(index, key, culture)!");
        source.AppendLine("            : manager.GetString(key, culture)!;");
        source.AppendLine("    }");
        source.AppendLine("    private static int GetResourceIndex(string key) => key switch");
        source.AppendLine("    {");
        for (int i = 0; i < entries.Count; i++)
        {
            source.Append("        ").Append(Literal(entries[i].Name)).Append(" => ").Append(i).AppendLine(",");
        }

        source.AppendLine("        _ => -1");
        source.AppendLine("    };");
        source.AppendLine("    internal static class Resources");
        source.AppendLine("    {");
        source.AppendLine("        private static class LookupHolder");
        source.AppendLine("        {");
        source.AppendLine("            internal static readonly global::System.Func<global::System.Resources.ResourceManager> GetManager =");
        source.AppendLine("                static () => ResourceManager;");
        source.AppendLine("        }");
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            source.Append("        internal static global::Microsoft.Build.Framework.Resources.StringResource @")
                .Append(entry.Identifier).Append(" => new(").Append(Literal(entry.Name)).Append(", ")
                .Append(i).AppendLine(", LookupHolder.GetManager);");
        }

        source.AppendLine("    }");
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            source.Append("    internal static string @").Append(entry.Identifier)
                .Append(" => GetResourceString(").Append(i).Append(", ").Append(Literal(entry.Name)).AppendLine(", Culture);");
            if (entry.Arguments == 0)
            {
                continue;
            }

            source.Append("    internal static string Format").Append(entry.Identifier).Append('(');
            for (int argument = 0; argument < entry.Arguments; argument++)
            {
                if (argument > 0)
                {
                    source.Append(", ");
                }

                source.Append("object? p").Append(argument);
            }

            source.AppendLine(")");
            source.AppendLine("    {");
            source.AppendLine("        var culture = Culture;");
            source.Append("        return global::System.String.Format(culture, GetResourceString(")
                .Append(i).Append(", ").Append(Literal(entry.Name)).Append(", culture)");
            for (int argument = 0; argument < entry.Arguments; argument++)
            {
                source.Append(", p").Append(argument);
            }

            source.AppendLine(");");
            source.AppendLine("    }");
        }

        source.AppendLine("}");
        context.AddSource(input.ClassName + ".g.cs", SourceText.From(source.ToString(), Encoding.UTF8, SourceHashAlgorithm.Sha256));
    }

    private static int ArgumentCount(string value)
    {
        int count = 0;
        foreach (Match match in Regex.Matches(value, @"(?<!\{)\{([0-9]+)(?:[^}]*)\}(?!\})"))
        {
            if (int.TryParse(match.Groups[1].Value, out int index) && index < 64)
            {
                count = Math.Max(count, index + 1);
            }
        }

        return count;
    }

    private static bool ValidQualifiedName(string name)
    {
        foreach (string part in name.Split('.'))
        {
            if (!SyntaxFacts.IsValidIdentifier(part))
            {
                return false;
            }
        }

        return true;
    }

    private static string Identifier(string name)
    {
        var result = new StringBuilder();
        if (!SyntaxFacts.IsIdentifierStartCharacter(name[0]))
        {
            result.Append('_');
        }

        foreach (char character in name)
        {
            result.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');
        }

        return result.ToString();
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static void Report(SourceProductionContext context, Input input, string reason)
        => context.ReportDiagnostic(Diagnostic.Create(s_invalidCatalog, Location.None, input.Path, reason));

    private sealed class Input(string path, SourceText? text, string className, string? logicalName, bool formatMethods)
    {
        internal string Path { get; } = path;
        internal SourceText? Text { get; } = text;
        internal string ClassName { get; } = className;
        internal string? LogicalName { get; } = logicalName;
        internal bool FormatMethods { get; } = formatMethods;
    }

    private sealed class Entry(string name, string identifier, int arguments)
    {
        internal string Name { get; } = name;
        internal string Identifier { get; } = identifier;
        internal int Arguments { get; } = arguments;
    }
}
