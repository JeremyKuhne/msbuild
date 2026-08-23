// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

namespace MSBuild.Benchmarks;

[MemoryDiagnoser]
public class ConditionEvaluationBenchmark
{
    public enum ConditionShape
    {
        Simple,
        Compound,
    }

    [Params(ConditionShape.Simple, ConditionShape.Compound)]
    public ConditionShape Shape { get; set; }

    private Expander<ProjectPropertyInstance, ProjectItemInstance> _expander = null!;
    private string _condition = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        PropertyDictionary<ProjectPropertyInstance> properties = new();
        properties.Set(ProjectPropertyInstance.Create("Configuration", "Release"));
        properties.Set(ProjectPropertyInstance.Create("Platform", "AnyCPU"));

        _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
            properties,
            new ItemDictionary<ProjectItemInstance>(),
            new StringMetadataTable(new Dictionary<string, string>()),
            FileSystems.Default);
        _condition = Shape switch
        {
            ConditionShape.Simple => "'$(Configuration)' == 'Release'",
            ConditionShape.Compound => "'$(Configuration)' == 'Release' And '$(Platform)' == 'AnyCPU'",
            _ => throw new InvalidOperationException(),
        };

        EvaluateCondition();
    }

    [Benchmark]
    public bool EvaluateCondition()
        => ConditionEvaluator.EvaluateCondition(
            _condition,
            ParserOptions.AllowProperties,
            _expander,
            ExpanderOptions.ExpandProperties,
            Environment.CurrentDirectory,
            ElementLocation.EmptyLocation,
            FileSystems.Default,
            loggingContext: null);
}