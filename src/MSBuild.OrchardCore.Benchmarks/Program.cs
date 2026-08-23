// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using MSBuild.OrchardCore.Benchmarks;
using static MSBuild.Benchmarks.Extensions;

var argList = new List<string>(args);

ParseAndRemoveBooleanParameter(argList, "--collect-etw", out bool collectEtw);
ParseAndRemoveBooleanParameter(argList, "--disable-ngen", out bool disableNGen);
ParseAndRemoveBooleanParameter(argList, "--disable-inlining", out bool disableJitInlining);
ParseAndRemoveBooleanParameter(argList, "--project-graph", out bool projectGraph);
if (!TryParseAndRemoveStringParameter(
        argList,
        "--orchard-core-project",
        out string? projectPath,
        out string? error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--orchard-core-repository",
        out string? repositoryPath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--semantic-manifest",
        out string? semanticManifestPath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--evaluation-profile",
        out string? evaluationProfilePath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--repository-measurement",
        out string? repositoryMeasurementPath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--project-subset",
        out string? projectSubsetPath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--wildcard-trace",
        out string? wildcardTracePath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--evaluation-stage",
        out string? evaluationStageName,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--sharing-policy",
        out string? sharingPolicyName,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--project-graph-measurement",
        out string? projectGraphMeasurementPath,
        out error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--degree-of-parallelism",
        out string? degreeOfParallelismValue,
        out error))
{
    Console.Error.WriteLine(error);
    return 1;
}

if (projectPath is null && repositoryPath is null)
{
    Console.Error.WriteLine(
        "Specify --orchard-core-project <path> or --orchard-core-repository <path>.");
    return 1;
}

if (projectPath is not null && repositoryPath is not null)
{
    Console.Error.WriteLine(
        "Specify either --orchard-core-project or --orchard-core-repository, not both.");
    return 1;
}

if (semanticManifestPath is null &&
    evaluationProfilePath is null &&
    repositoryMeasurementPath is null &&
    (evaluationStageName is not null || sharingPolicyName is not null))
{
    Console.Error.WriteLine(
        "Specify --evaluation-stage and --sharing-policy only with --semantic-manifest, --evaluation-profile, or --repository-measurement.");
    return 1;
}

if (degreeOfParallelismValue is not null &&
    projectGraphMeasurementPath is null &&
    !(projectGraph && evaluationProfilePath is not null))
{
    Console.Error.WriteLine(
        "Specify --degree-of-parallelism only with --project-graph-measurement or a project graph evaluation profile.");
    return 1;
}

int oneShotModeCount = (semanticManifestPath is null ? 0 : 1) +
    (evaluationProfilePath is null ? 0 : 1) +
    (repositoryMeasurementPath is null ? 0 : 1) +
    (projectGraphMeasurementPath is null ? 0 : 1);
if (oneShotModeCount > 1)
{
    Console.Error.WriteLine(
        "Specify only one of --semantic-manifest, --evaluation-profile, --repository-measurement, or --project-graph-measurement.");
    return 1;
}

if (wildcardTracePath is not null && oneShotModeCount == 0)
{
    Console.Error.WriteLine(
        "Specify --wildcard-trace only with a one-shot manifest, profile, or measurement mode.");
    return 1;
}

if ((oneShotModeCount > 0 || projectGraph) &&
    repositoryPath is null)
{
    Console.Error.WriteLine(
        "Specify semantic manifests, evaluation profiles, and project graph workloads with --orchard-core-repository.");
    return 1;
}

if (projectGraph && (semanticManifestPath is not null || projectGraphMeasurementPath is not null))
{
    Console.Error.WriteLine(
        "Do not combine --project-graph with a semantic manifest or project graph measurement.");
    return 1;
}

if (projectGraph && repositoryMeasurementPath is not null)
{
    Console.Error.WriteLine(
        "Use --project-graph-measurement for project graph measurements.");
    return 1;
}

if (projectGraph &&
    evaluationProfilePath is not null &&
    (evaluationStageName is not null || sharingPolicyName is not null))
{
    Console.Error.WriteLine(
        "Project graph evaluation profiles use the graph's Full stage and Shared policy; do not specify --evaluation-stage or --sharing-policy.");
    return 1;
}

if (projectSubsetPath is not null && (repositoryPath is null || projectGraph || projectGraphMeasurementPath is not null))
{
    Console.Error.WriteLine(
        "Specify --project-subset only with repository evaluation, semantic manifest, or evaluation profile modes.");
    return 1;
}

string? solutionPath = null;
if (projectPath is not null)
{
    projectPath = Path.GetFullPath(projectPath);
    if (!File.Exists(projectPath))
    {
        Console.Error.WriteLine($"The Orchard Core project does not exist: {projectPath}");
        return 1;
    }
}
else
{
    repositoryPath = Path.GetFullPath(repositoryPath!);
    if (!Directory.Exists(repositoryPath))
    {
        Console.Error.WriteLine($"The Orchard Core repository does not exist: {repositoryPath}");
        return 1;
    }

    solutionPath = Path.Combine(repositoryPath, "OrchardCore.slnx");
    if (!File.Exists(solutionPath))
    {
        Console.Error.WriteLine($"The Orchard Core solution does not exist: {solutionPath}");
        return 1;
    }

    if (projectSubsetPath is not null)
    {
        projectSubsetPath = Path.GetFullPath(projectSubsetPath);
        if (!File.Exists(projectSubsetPath))
        {
            Console.Error.WriteLine($"The project subset manifest does not exist: {projectSubsetPath}");
            return 1;
        }
    }
}

using TextWriterTraceListener? wildcardTraceListener = CreateWildcardTraceListener(wildcardTracePath);

string sdkPath;
try
{
    sdkPath = DotNetSdkLocator.FindSdkPath();
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

Environment.SetEnvironmentVariable(
    OrchardCoreEvaluationBenchmark.ProjectPathEnvironmentVariable,
    projectPath);
Environment.SetEnvironmentVariable(
    OrchardCoreRepositoryEvaluationBenchmark.SolutionPathEnvironmentVariable,
    solutionPath);
Environment.SetEnvironmentVariable(
    OrchardCoreRepositoryEvaluationBenchmark.ProjectSubsetPathEnvironmentVariable,
    projectSubsetPath);
Environment.SetEnvironmentVariable(
    OrchardCoreEvaluationBenchmark.DotNetSdkPathEnvironmentVariable,
    sdkPath);
Environment.SetEnvironmentVariable("MSBUILDTERMINALLOGGER", "off");

if (semanticManifestPath is not null)
{
    if (!TryParseEnum(
            evaluationStageName,
            ProjectEvaluationStage.Full,
            "--evaluation-stage",
            out ProjectEvaluationStage evaluationStage,
            out error) ||
        !TryParseEnum(
            sharingPolicyName,
            EvaluationContext.SharingPolicy.Shared,
            "--sharing-policy",
            out EvaluationContext.SharingPolicy sharingPolicy,
            out error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    try
    {
        OrchardCoreRepositoryEvaluationBenchmark.WriteSemanticManifest(
            Path.GetFullPath(semanticManifestPath),
            evaluationStage,
            sharingPolicy);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }

    return 0;
}

if (evaluationProfilePath is not null)
{
    if (projectGraph)
    {
        if (!TryParseDegreeOfParallelism(degreeOfParallelismValue, out int? degreeOfParallelism, out error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        try
        {
            OrchardCoreProjectGraphBenchmark.WriteEvaluationProfile(
                Path.GetFullPath(evaluationProfilePath),
                degreeOfParallelism);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
    else
    {
        if (!TryParseEnum(
                evaluationStageName,
                ProjectEvaluationStage.Items,
                "--evaluation-stage",
                out ProjectEvaluationStage evaluationStage,
                out error) ||
            !TryParseEnum(
                sharingPolicyName,
                EvaluationContext.SharingPolicy.Shared,
                "--sharing-policy",
                out EvaluationContext.SharingPolicy sharingPolicy,
                out error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        try
        {
            OrchardCoreRepositoryEvaluationBenchmark.WriteEvaluationProfile(
                Path.GetFullPath(evaluationProfilePath),
                evaluationStage,
                sharingPolicy);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    return 0;
}

if (repositoryMeasurementPath is not null)
{
    if (!TryParseEnum(
            evaluationStageName,
            ProjectEvaluationStage.Items,
            "--evaluation-stage",
            out ProjectEvaluationStage evaluationStage,
            out error) ||
        !TryParseEnum(
            sharingPolicyName,
            EvaluationContext.SharingPolicy.Shared,
            "--sharing-policy",
            out EvaluationContext.SharingPolicy sharingPolicy,
            out error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    try
    {
        OrchardCoreRepositoryEvaluationBenchmark.WriteMeasurement(
            Path.GetFullPath(repositoryMeasurementPath),
            evaluationStage,
            sharingPolicy);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }

    return 0;
}

if (projectGraphMeasurementPath is not null)
{
    if (!TryParseDegreeOfParallelism(degreeOfParallelismValue, out int? degreeOfParallelism, out error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    try
    {
        OrchardCoreProjectGraphBenchmark.WriteMeasurement(
            Path.GetFullPath(projectGraphMeasurementPath),
            degreeOfParallelism);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }

    return 0;
}

IConfig config = GetConfig(collectEtw, disableNGen, disableJitInlining);
bool hasErrors = solutionPath is null
    ? BenchmarkRunner.Run<OrchardCoreEvaluationBenchmark>(config, [.. argList]).HasAnyErrors()
    : projectGraph
        ? BenchmarkRunner.Run<OrchardCoreProjectGraphBenchmark>(config, [.. argList]).HasAnyErrors()
        : BenchmarkRunner.Run<OrchardCoreRepositoryEvaluationBenchmark>(config, [.. argList]).HasAnyErrors();

return hasErrors
    ? 1
    : 0;

static IConfig GetConfig(bool collectEtw, bool disableNGen, bool disableJitInlining)
{
    if (Debugger.IsAttached)
    {
        return new DebugInProcessConfig();
    }

    IConfig config = DefaultConfig.Instance;

    if (collectEtw)
    {
        config = config.AddDiagnoser(new EtwProfiler());
    }

    // Use a mutator for settings that should apply to all jobs
    // (default or CLI-specified like --job short).
    Job overrides = new Job()
        .DontEnforcePowerPlan();

    if (disableNGen)
    {
        overrides = overrides
            .WithEnvironmentVariable("COMPlus_ZapDisable", "1")
            .WithEnvironmentVariable("COMPlus_ReadyToRun", "0")
            .WithEnvironmentVariable("DOTNET_ReadyToRun", "0");
    }

    if (disableJitInlining)
    {
        overrides = overrides
            .WithEnvironmentVariable("COMPlus_JitNoInline", "1")
            .WithEnvironmentVariable("DOTNET_JitNoInline", "1");
    }

    config = config.AddJob(overrides.AsMutator());

    return config;
}

static void ParseAndRemoveBooleanParameter(List<string> argsList, string parameter, out bool parameterValue)
{
    int parameterIndex = argsList.IndexOf(parameter);

    if (parameterIndex != -1)
    {
        argsList.RemoveAt(parameterIndex);

        parameterValue = true;
    }
    else
    {
        parameterValue = false;
    }
}

static bool TryParseAndRemoveStringParameter(
    List<string> argsList,
    string parameter,
    out string? parameterValue,
    out string? error)
{
    int parameterIndex = argsList.IndexOf(parameter);

    if (parameterIndex == -1)
    {
        parameterValue = null;
        error = null;
        return true;
    }

    if (parameterIndex == argsList.Count - 1 ||
        string.IsNullOrWhiteSpace(argsList[parameterIndex + 1]) ||
        argsList[parameterIndex + 1].StartsWith('-'))
    {
        parameterValue = null;
        error = $"Missing value for {parameter}.";
        return false;
    }

    parameterValue = argsList[parameterIndex + 1];
    argsList.RemoveRange(parameterIndex, 2);
    error = null;
    return true;
}

static bool TryParseEnum<T>(
    string? value,
    T defaultValue,
    string parameter,
    out T result,
    out string? error)
    where T : struct, Enum
{
    if (value is null)
    {
        result = defaultValue;
        error = null;
        return true;
    }

    if (Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result))
    {
        error = null;
        return true;
    }

    error = $"Invalid value for {parameter}: {value}. Expected one of: {string.Join(", ", Enum.GetNames<T>())}.";
    return false;
}

static bool TryParseDegreeOfParallelism(
    string? value,
    out int? degreeOfParallelism,
    out string? error)
{
    if (value is null || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
    {
        degreeOfParallelism = null;
        error = null;
        return true;
    }

    if (int.TryParse(value, out int parsedValue) && parsedValue > 0)
    {
        degreeOfParallelism = parsedValue;
        error = null;
        return true;
    }

    degreeOfParallelism = null;
    error = $"Invalid value for --degree-of-parallelism: {value}. Expected default or a positive integer.";
    return false;
}

static TextWriterTraceListener? CreateWildcardTraceListener(string? outputPath)
{
    if (outputPath is null)
    {
        return null;
    }

    outputPath = Path.GetFullPath(outputPath);
    string? outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    File.Delete(outputPath);
    Environment.SetEnvironmentVariable("MSBUILDENABLEDEBUGTRACING", "1");
    Environment.SetEnvironmentVariable("MSBUILDLOGEXPANDEDWILDCARDS", "1");

    TextWriterTraceListener listener = new(outputPath);
    Trace.Listeners.Add(listener);
    Trace.AutoFlush = true;
    return listener;
}
