// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

namespace MSBuild.Benchmarks;

[MemoryDiagnoser]
public class PropertyGroupEvaluationBenchmark
{
    [Params(256)]
    public int PropertyGroupCount { get; set; }

    private string _projectPath = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        StringBuilder builder = new();
        builder.AppendLine("<Project>");
        builder.AppendLine("  <PropertyGroup><Seed>Value</Seed></PropertyGroup>");

        for (int index = 0; index < PropertyGroupCount; index++)
        {
            builder.AppendLine("  <PropertyGroup Condition=\"'$(Seed)' == 'Value'\">");
            builder.AppendLine($"    <Property{index}>$(Seed)-{index}</Property{index}>");
            builder.AppendLine($"    <ChainedProperty{index}>$(Property{index})</ChainedProperty{index}>");
            builder.AppendLine("  </PropertyGroup>");
        }

        builder.AppendLine("</Project>");

        _projectPath = Path.Combine(Path.GetTempPath(), $"property-group-bench-{Guid.NewGuid():N}.proj");
        File.WriteAllText(_projectPath, builder.ToString());
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_projectPath is not null && File.Exists(_projectPath))
        {
            File.Delete(_projectPath);
        }
    }

    [Benchmark]
    public string EvaluateProperties()
    {
        using ProjectCollection collection = new();
        ProjectInstance project = ProjectInstance.FromFile(_projectPath, new ProjectOptions
        {
            ProjectCollection = collection,
            EvaluationStage = ProjectEvaluationStage.Properties,
        });

        return project.GetPropertyValue($"ChainedProperty{PropertyGroupCount - 1}");
    }
}