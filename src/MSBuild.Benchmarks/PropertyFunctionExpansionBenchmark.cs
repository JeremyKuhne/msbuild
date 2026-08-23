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
public class PropertyFunctionExpansionBenchmark
{
    public enum FunctionShape
    {
        OneArgument,
        TwoArguments,
        FourArguments,
        QuotedComma,
    }

    [Params(FunctionShape.OneArgument, FunctionShape.TwoArguments, FunctionShape.FourArguments, FunctionShape.QuotedComma)]
    public FunctionShape Shape { get; set; }

    private ProjectCollection _projectCollection = null!;
    private Expander<ProjectPropertyInstance, ProjectItemInstance> _expander = null!;
    private IElementLocation _location = null!;
    private string _expression = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _projectCollection = new ProjectCollection();
        ProjectRootElement xml = ProjectRootElement.Create(_projectCollection);
        xml.FullPath = Path.Combine(Path.GetTempPath(), "property-function-benchmark.proj");
        ProjectInstance project = new(xml, globalProperties: null, toolsVersion: null, _projectCollection);

        _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
            new PropertyDictionary<ProjectPropertyInstance>(),
            new ItemDictionary<ProjectItemInstance>(),
            new StringMetadataTable(new Dictionary<string, string>()),
            FileSystems.Default);
        _location = ElementLocation.EmptyLocation;
        _expression = Shape switch
        {
            FunctionShape.OneArgument => "$([System.IO.Path]::GetFileName('src/file.cs'))",
            FunctionShape.TwoArguments => "$([System.IO.Path]::Combine('src', 'file.cs'))",
            FunctionShape.FourArguments => "$([System.String]::Concat('a', 'b', 'c', 'd'))",
            FunctionShape.QuotedComma => "$([System.String]::Copy('a,b').Replace(',', ';'))",
            _ => throw new InvalidOperationException(),
        };

        GC.KeepAlive(project);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _projectCollection.Dispose();

    [Benchmark]
    public string ExpandPropertyFunction()
        => _expander.ExpandIntoStringLeaveEscaped(
            _expression,
            ExpanderOptions.ExpandProperties,
            _location);
}