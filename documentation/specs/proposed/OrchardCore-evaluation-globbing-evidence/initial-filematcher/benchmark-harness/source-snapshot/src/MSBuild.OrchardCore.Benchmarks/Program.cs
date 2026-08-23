// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using MSBuild.OrchardCore.Benchmarks;
using static MSBuild.Benchmarks.Extensions;

var argList = new List<string>(args);

ParseAndRemoveBooleanParameter(argList, "--collect-etw", out bool collectEtw);
ParseAndRemoveBooleanParameter(argList, "--disable-ngen", out bool disableNGen);
ParseAndRemoveBooleanParameter(argList, "--disable-inlining", out bool disableJitInlining);
if (!TryParseAndRemoveStringParameter(
        argList,
        "--orchard-core-project",
        out string? projectPath,
        out string? error) ||
    !TryParseAndRemoveStringParameter(
        argList,
        "--orchard-core-repository",
        out string? repositoryPath,
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
}

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
    OrchardCoreEvaluationBenchmark.DotNetSdkPathEnvironmentVariable,
    sdkPath);
Environment.SetEnvironmentVariable("MSBUILDTERMINALLOGGER", "off");

IConfig config = GetConfig(collectEtw, disableNGen, disableJitInlining);
bool hasErrors = solutionPath is null
    ? BenchmarkRunner.Run<OrchardCoreEvaluationBenchmark>(config, [.. argList]).HasAnyErrors()
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
