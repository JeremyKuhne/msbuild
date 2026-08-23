// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;

namespace MSBuild.OrchardCore.Benchmarks;

/// <summary>
/// Measures sequential evaluation of every MSBuild project in the Orchard Core solution.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class OrchardCoreRepositoryEvaluationBenchmark
{
    internal const string SolutionPathEnvironmentVariable = "MSBUILD_BENCHMARK_ORCHARDCORE_SOLUTION";

    private Dictionary<string, string> _globalProperties = null!;
    private string[] _projectPaths = null!;
    private string? _originalMSBuildSDKsPath;
    private string? _originalMSBuildExtensionsPath;
    private string? _originalMSBuildEnableWorkloadResolver;
    private string? _originalAdditionalSdkResolversFolder;

    [GlobalSetup]
    public void GlobalSetup()
    {
        string? solutionPath = Environment.GetEnvironmentVariable(SolutionPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new InvalidOperationException(
                "The Orchard Core solution path was not passed to the benchmark process.");
        }

        solutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("The Orchard Core benchmark solution does not exist.", solutionPath);
        }

        string? sdkPath = Environment.GetEnvironmentVariable(
            OrchardCoreEvaluationBenchmark.DotNetSdkPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            throw new InvalidOperationException(
                "The .NET SDK path was not passed to the benchmark process.");
        }

        _originalMSBuildSDKsPath = Environment.GetEnvironmentVariable("MSBuildSDKsPath");
        _originalMSBuildExtensionsPath = Environment.GetEnvironmentVariable("MSBuildExtensionsPath");
        _originalMSBuildEnableWorkloadResolver = Environment.GetEnvironmentVariable("MSBuildEnableWorkloadResolver");
        _originalAdditionalSdkResolversFolder = Environment.GetEnvironmentVariable(
            "MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET");

        try
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdkPath, "Sdks"));
            Environment.SetEnvironmentVariable("MSBuildExtensionsPath", sdkPath);
            Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver", "false");
            Environment.SetEnvironmentVariable(
                "MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET",
                Path.Combine(sdkPath, "SdkResolvers"));

            _globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MSBuildEnableWorkloadResolver"] = "false",
                ["NuGetRestoreTargets"] = Path.Combine(sdkPath, "NuGet.targets"),
            };

            SolutionFile solution = SolutionFile.Parse(solutionPath);
            _projectPaths = solution.ProjectsInOrder
                .Where(project => project.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
                .Select(project => project.AbsolutePath)
                .ToArray();

            if (_projectPaths.Length == 0)
            {
                throw new InvalidOperationException("The Orchard Core solution contains no MSBuild projects.");
            }

            string? missingProject = _projectPaths.FirstOrDefault(path => !File.Exists(path));
            if (missingProject is not null)
            {
                throw new FileNotFoundException(
                    "An Orchard Core solution project does not exist.",
                    missingProject);
            }

            ValidateEquivalentResults(ProjectEvaluationStage.Items);
            ValidateEquivalentResults(ProjectEvaluationStage.Full);
        }
        catch
        {
            RestoreProcessState();
            throw;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        RestoreProcessState();
    }

    [Benchmark]
    [BenchmarkCategory("Items")]
    public long ItemsIsolated()
        => EvaluateSolution(ProjectEvaluationStage.Items, EvaluationContext.SharingPolicy.Isolated);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Items")]
    public long ItemsSharedSdkCache()
        => EvaluateSolution(ProjectEvaluationStage.Items, EvaluationContext.SharingPolicy.SharedSDKCache);

    [Benchmark]
    [BenchmarkCategory("Items")]
    public long ItemsShared()
        => EvaluateSolution(ProjectEvaluationStage.Items, EvaluationContext.SharingPolicy.Shared);

    [Benchmark]
    [BenchmarkCategory("Full")]
    public long FullIsolated()
        => EvaluateSolution(ProjectEvaluationStage.Full, EvaluationContext.SharingPolicy.Isolated);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Full")]
    public long FullSharedSdkCache()
        => EvaluateSolution(ProjectEvaluationStage.Full, EvaluationContext.SharingPolicy.SharedSDKCache);

    [Benchmark]
    [BenchmarkCategory("Full")]
    public long FullShared()
        => EvaluateSolution(ProjectEvaluationStage.Full, EvaluationContext.SharingPolicy.Shared);

    private long EvaluateSolution(
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy,
        List<EvaluatedValue>? evaluatedValues = null)
    {
        using ProjectCollection collection = new(_globalProperties);
        EvaluationContext evaluationContext = EvaluationContext.Create(sharingPolicy);
        long checksum = 17;

        foreach (string projectPath in _projectPaths)
        {
            ProjectInstance project = ProjectInstance.FromFile(projectPath, new ProjectOptions
            {
                ProjectCollection = collection,
                EvaluationContext = evaluationContext,
                EvaluationStage = evaluationStage,
                GlobalProperties = _globalProperties,
            });

            evaluatedValues?.Add(new('F', project.FullPath, string.Empty));
            checksum = unchecked((checksum * 31) + project.FullPath.Length);
            checksum = unchecked((checksum * 31) + project.Properties.Count);
            checksum = unchecked((checksum * 31) + project.Items.Count);

            if (evaluatedValues is not null)
            {
                foreach (ProjectPropertyInstance property in project.Properties)
                {
                    evaluatedValues.Add(new('P', property.Name, property.EvaluatedValue));
                }

                foreach (ProjectItemInstance item in project.Items)
                {
                    evaluatedValues.Add(new('I', item.ItemType, item.EvaluatedInclude));
                }
            }

            if (evaluationStage == ProjectEvaluationStage.Full)
            {
                checksum = unchecked((checksum * 31) + project.Targets.Count);

                if (evaluatedValues is not null)
                {
                    foreach (string targetName in project.Targets.Keys)
                    {
                        evaluatedValues.Add(new('T', targetName, string.Empty));
                    }
                }
            }
        }

        return checksum;
    }

    private void ValidateEquivalentResults(ProjectEvaluationStage evaluationStage)
    {
        List<EvaluatedValue> expectedValues = [];
        long expected = EvaluateSolution(
            evaluationStage,
            EvaluationContext.SharingPolicy.SharedSDKCache,
            expectedValues);
        EvaluationContext.SharingPolicy[] sharingPolicies =
            [EvaluationContext.SharingPolicy.Isolated, EvaluationContext.SharingPolicy.Shared];

        foreach (EvaluationContext.SharingPolicy sharingPolicy in sharingPolicies)
        {
            List<EvaluatedValue> actualValues = [];
            long actual = EvaluateSolution(evaluationStage, sharingPolicy, actualValues);
            if (actual != expected || !actualValues.SequenceEqual(expectedValues))
            {
                throw new InvalidOperationException(
                    $"Orchard Core evaluation results differ for {evaluationStage} evaluation: " +
                    $"SharedSDKCache={expected}, {sharingPolicy}={actual}.");
            }
        }
    }

    private void RestoreProcessState()
    {
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", _originalMSBuildSDKsPath);
        Environment.SetEnvironmentVariable("MSBuildExtensionsPath", _originalMSBuildExtensionsPath);
        Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver", _originalMSBuildEnableWorkloadResolver);
        Environment.SetEnvironmentVariable(
            "MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET",
            _originalAdditionalSdkResolversFolder);
    }

    private readonly record struct EvaluatedValue(char Kind, string Name, string Value);
}