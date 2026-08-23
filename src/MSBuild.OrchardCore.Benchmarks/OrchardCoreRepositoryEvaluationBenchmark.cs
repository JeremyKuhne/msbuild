// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Logging;

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
    internal const string ProjectSubsetPathEnvironmentVariable = "MSBUILD_BENCHMARK_ORCHARDCORE_PROJECT_SUBSET";

    private static readonly string[] s_builtInMetadataNames =
    [
        "FullPath",
        "RootDir",
        "Filename",
        "Extension",
        "RelativeDir",
        "Directory",
        "RecursiveDir",
        "DefiningProjectFullPath",
        "DefiningProjectDirectory",
        "DefiningProjectName",
        "DefiningProjectExtension",
    ];

    private Dictionary<string, string> _globalProperties = null!;
    private string[] _projectPaths = null!;
    private string _repositoryPath = null!;
    private string _sdkPath = null!;
    private string _solutionPath = null!;
    private RootCanonicalizer _rootCanonicalizer = null!;
    private string? _originalMSBuildSDKsPath;
    private string? _originalMSBuildExtensionsPath;
    private string? _originalMSBuildEnableWorkloadResolver;
    private string? _originalAdditionalSdkResolversFolder;

    [GlobalSetup]
    public void GlobalSetup() => Initialize(validateEvaluationResults: true);

    internal Dictionary<string, string> GlobalProperties => _globalProperties;

    internal string SolutionPath => _solutionPath;

    internal void Initialize(bool validateEvaluationResults)
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
            _solutionPath = solutionPath;
            _repositoryPath = Path.GetDirectoryName(solutionPath)!;
            _sdkPath = Path.GetFullPath(sdkPath);
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

            string? projectSubsetPath = Environment.GetEnvironmentVariable(ProjectSubsetPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(projectSubsetPath))
            {
                _projectPaths = ReadProjectSubset(projectSubsetPath, _projectPaths);
            }

            _rootCanonicalizer = CreateRootCanonicalizer();
            if (validateEvaluationResults)
            {
                ValidateEquivalentResults(ProjectEvaluationStage.Items);
                ValidateEquivalentResults(ProjectEvaluationStage.Full);
            }
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

    internal string Canonicalize(string value) => _rootCanonicalizer.Canonicalize(value);

    internal static void WriteSemanticManifest(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        OrchardCoreRepositoryEvaluationBenchmark benchmark = new();
        benchmark.GlobalSetup();

        try
        {
            benchmark.WriteSemanticManifestCore(outputPath, evaluationStage, sharingPolicy);
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
    }

    internal static void WriteEvaluationProfile(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        OrchardCoreRepositoryEvaluationBenchmark benchmark = new();
        benchmark.Initialize(validateEvaluationResults: false);

        try
        {
            benchmark.WriteEvaluationProfileCore(outputPath, evaluationStage, sharingPolicy);
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
    }

    internal static void WriteMeasurement(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        OrchardCoreRepositoryEvaluationBenchmark benchmark = new();
        benchmark.Initialize(validateEvaluationResults: false);

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch stopwatch = Stopwatch.StartNew();
            long checksum = benchmark.EvaluateSolution(evaluationStage, sharingPolicy);
            stopwatch.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            benchmark.WriteMeasurementFile(
                outputPath,
                evaluationStage,
                sharingPolicy,
                checksum,
                stopwatch.Elapsed,
                allocatedBytes);
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
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

    private void WriteSemanticManifestCore(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = $"{outputPath}.{Environment.ProcessId}.tmp";
        File.Delete(temporaryPath);

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    WriteSemanticManifest(writer, evaluationStage, sharingPolicy);
                }

                stream.WriteByte((byte)'\n');
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void WriteEvaluationProfileCore(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.Delete(outputPath);
        ProfilerLogger profilerLogger = new(outputPath);
        using ProjectCollection collection = new(
            _globalProperties,
            [profilerLogger],
            ToolsetDefinitionLocations.Default);

        try
        {
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
                    LoadSettings = ProjectLoadSettings.ProfileEvaluation,
                });

                checksum = unchecked((checksum * 31) + project.FullPath.Length);
                checksum = unchecked((checksum * 31) + project.Properties.Count);
                checksum = unchecked((checksum * 31) + project.Items.Count);
                if (evaluationStage == ProjectEvaluationStage.Full)
                {
                    checksum = unchecked((checksum * 31) + project.Targets.Count);
                }
            }

            GC.KeepAlive(checksum);
        }
        finally
        {
            collection.UnregisterAllLoggers();
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"The evaluation profiler did not write its report: {outputPath}");
        }
    }

    private void WriteMeasurementFile(
        string outputPath,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy,
        long checksum,
        TimeSpan elapsed,
        long allocatedBytes)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = $"{outputPath}.{Environment.ProcessId}.tmp";
        File.Delete(temporaryPath);

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("workload", "RepositoryEvaluation");
                    writer.WriteNumber("projectCount", _projectPaths.Length);
                    writer.WriteString("evaluationStage", evaluationStage.ToString());
                    writer.WriteString("sharingPolicy", sharingPolicy.ToString());
                    writer.WriteNumber("elapsedTicks", elapsed.Ticks);
                    writer.WriteNumber("allocatedBytes", allocatedBytes);
                    writer.WriteNumber("checksum", checksum);
                    writer.WriteEndObject();
                }

                stream.WriteByte((byte)'\n');
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private string[] ReadProjectSubset(string projectSubsetPath, string[] solutionProjectPaths)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(projectSubsetPath));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out int parsedSchemaVersion) ||
            parsedSchemaVersion != 1 ||
            !root.TryGetProperty("projects", out JsonElement projects) ||
            projects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The project subset manifest must have schemaVersion 1 and a projects array.");
        }

        Dictionary<string, string> solutionProjects = new(PathComparer);
        foreach (string solutionProjectPath in solutionProjectPaths)
        {
            solutionProjects.Add(Path.GetFullPath(solutionProjectPath), solutionProjectPath);
        }

        List<string> selectedProjects = new(projects.GetArrayLength());
        HashSet<string> seenProjects = new(PathComparer);
        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(project.GetString()))
            {
                throw new InvalidDataException(
                    "Every projects entry in the project subset manifest must be a non-empty string.");
            }

            string relativePath = project.GetString()!;
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException(
                    $"Project subset paths must be relative to the Orchard Core repository: {relativePath}");
            }

            relativePath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (relativePath.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Project subset paths cannot contain parent traversal: {relativePath}");
            }

            string fullPath = Path.GetFullPath(Path.Combine(_repositoryPath, relativePath));
            if (!solutionProjects.TryGetValue(fullPath, out string? solutionProjectPath))
            {
                throw new InvalidDataException(
                    $"The project subset entry is not an MSBuild project in OrchardCore.slnx: {relativePath}");
            }

            if (!seenProjects.Add(fullPath))
            {
                throw new InvalidDataException(
                    $"The project subset contains a duplicate project: {relativePath}");
            }

            selectedProjects.Add(solutionProjectPath);
        }

        if (selectedProjects.Count == 0)
        {
            throw new InvalidDataException("The project subset manifest contains no projects.");
        }

        return [.. selectedProjects];
    }

    private void WriteSemanticManifest(
        Utf8JsonWriter writer,
        ProjectEvaluationStage evaluationStage,
        EvaluationContext.SharingPolicy sharingPolicy)
    {
        using ProjectCollection collection = new(_globalProperties);
        EvaluationContext evaluationContext = EvaluationContext.Create(sharingPolicy);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("evaluationStage", evaluationStage.ToString());
        writer.WriteString("sharingPolicy", sharingPolicy.ToString());
        writer.WriteStartArray("projects");

        for (int projectIndex = 0; projectIndex < _projectPaths.Length; projectIndex++)
        {
            ProjectInstance project = ProjectInstance.FromFile(_projectPaths[projectIndex], new ProjectOptions
            {
                ProjectCollection = collection,
                EvaluationContext = evaluationContext,
                EvaluationStage = evaluationStage,
                GlobalProperties = _globalProperties,
            });

            WriteProject(writer, _rootCanonicalizer, project, projectIndex, evaluationStage);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProject(
        Utf8JsonWriter writer,
        RootCanonicalizer canonicalizer,
        ProjectInstance project,
        int projectIndex,
        ProjectEvaluationStage evaluationStage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("solutionIndex", projectIndex);
        writer.WriteString("path", canonicalizer.Canonicalize(project.FullPath));

        ProjectPropertyInstance[] properties = [.. project.Properties];
        Array.Sort(properties, static (left, right) => CompareNames(left.Name, right.Name));
        writer.WriteStartArray("properties");
        foreach (ProjectPropertyInstance property in properties)
        {
            writer.WriteStartObject();
            writer.WriteString("name", property.Name);
            writer.WriteString("value", canonicalizer.Canonicalize(property.EvaluatedValue));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("items");
        int itemIndex = 0;
        foreach (ProjectItemInstance item in project.Items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("evaluationIndex", itemIndex++);
            writer.WriteString("itemType", item.ItemType);
            writer.WriteString("identity", canonicalizer.Canonicalize(item.EvaluatedInclude));

            ProjectMetadataInstance[] metadata = [.. item.Metadata];
            Array.Sort(metadata, static (left, right) => CompareNames(left.Name, right.Name));
            writer.WriteStartArray("metadata");
            foreach (ProjectMetadataInstance metadataInstance in metadata)
            {
                writer.WriteStartObject();
                writer.WriteString("name", metadataInstance.Name);
                writer.WriteString(
                    "value",
                    canonicalizer.Canonicalize(metadataInstance.EvaluatedValue));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("builtInMetadata");
            foreach (string metadataName in s_builtInMetadataNames)
            {
                writer.WriteString(
                    metadataName,
                    canonicalizer.Canonicalize(item.GetMetadataValue(metadataName)));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("targets");
        if (evaluationStage == ProjectEvaluationStage.Full)
        {
            string[] targetNames = [.. project.Targets.Keys];
            Array.Sort(targetNames, CompareNames);
            foreach (string targetName in targetNames)
            {
                writer.WriteStringValue(targetName);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private RootCanonicalizer CreateRootCanonicalizer()
    {
        string nuGetPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        return new RootCanonicalizer(
        [
            new(_repositoryPath, "$(ORCHARD_ROOT)"),
            new(_sdkPath, "$(SDK_ROOT)"),
            new(AppContext.BaseDirectory, "$(MSBUILD_ROOT)"),
            new(nuGetPackagesPath, "$(NUGET_PACKAGES)"),
            new(Path.GetTempPath(), "$(TEMP_ROOT)"),
        ]);
    }

    private static int CompareNames(string left, string right)
    {
        int result = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return result != 0
            ? result
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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

    private readonly record struct CanonicalRoot(string Path, string Token);

    private sealed class RootCanonicalizer
    {
        private readonly CanonicalRoot[] _roots;

        internal RootCanonicalizer(CanonicalRoot[] roots)
        {
            List<CanonicalRoot> canonicalRoots = new(roots.Length);
            foreach (CanonicalRoot root in roots)
            {
                if (string.IsNullOrWhiteSpace(root.Path))
                {
                    continue;
                }

                string path = Path.GetFullPath(root.Path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (path.Length == 0)
                {
                    continue;
                }

                bool duplicate = false;
                foreach (CanonicalRoot existing in canonicalRoots)
                {
                    if (string.Equals(existing.Path, path, PathComparison))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    canonicalRoots.Add(new(path, root.Token));
                }
            }

            canonicalRoots.Sort(static (left, right) => right.Path.Length.CompareTo(left.Path.Length));
            _roots = [.. canonicalRoots];
        }

        internal string Canonicalize(string value)
        {
            foreach (CanonicalRoot root in _roots)
            {
                value = ReplaceRoot(value, root);
            }

            return value;
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private static string ReplaceRoot(string value, CanonicalRoot root)
        {
            int searchIndex = 0;
            while (searchIndex < value.Length)
            {
                int rootIndex = value.IndexOf(root.Path, searchIndex, PathComparison);
                if (rootIndex < 0)
                {
                    break;
                }

                int endIndex = rootIndex + root.Path.Length;
                if (endIndex < value.Length && !IsRootBoundary(value[endIndex]))
                {
                    searchIndex = endIndex;
                    continue;
                }

                value = string.Concat(
                    value.AsSpan(0, rootIndex),
                    root.Token,
                    value.AsSpan(endIndex));
                searchIndex = rootIndex + root.Token.Length;
            }

            return value;
        }

        private static bool IsRootBoundary(char character)
            => character == Path.DirectorySeparatorChar
                || character == Path.AltDirectorySeparatorChar
                || character is ';' or ',' or '"' or '\'' or ')' or ']';
    }
}