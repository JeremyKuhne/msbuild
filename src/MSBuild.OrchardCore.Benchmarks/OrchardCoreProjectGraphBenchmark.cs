// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;

namespace MSBuild.OrchardCore.Benchmarks;

/// <summary>
/// Measures construction of the Orchard Core project graph with a fresh project collection.
/// </summary>
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class OrchardCoreProjectGraphBenchmark
{
    private OrchardCoreRepositoryEvaluationBenchmark _environment = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _environment = new OrchardCoreRepositoryEvaluationBenchmark();
        _environment.Initialize(validateEvaluationResults: false);

        try
        {
            ValidateEquivalentGraphs();
        }
        catch
        {
            _environment.GlobalCleanup();
            throw;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _environment.GlobalCleanup();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ProjectGraph")]
    public long DegreeOne() => ConstructGraph(_environment, degreeOfParallelism: 1).Checksum;

    [Benchmark]
    [BenchmarkCategory("ProjectGraph")]
    public long DefaultParallelism() => ConstructGraph(_environment, degreeOfParallelism: null).Checksum;

    internal static void WriteMeasurement(string outputPath, int? degreeOfParallelism)
    {
        OrchardCoreRepositoryEvaluationBenchmark environment = new();
        environment.Initialize(validateEvaluationResults: false);

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch stopwatch = Stopwatch.StartNew();
            GraphRun graphRun = ConstructGraph(environment, degreeOfParallelism);
            stopwatch.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            GraphSnapshot snapshot = CreateSnapshot(environment, graphRun.Graph);
            WriteMeasurementFile(
                outputPath,
                graphRun,
                snapshot,
                stopwatch.Elapsed,
                allocatedBytes);
        }
        finally
        {
            environment.GlobalCleanup();
        }
    }

    internal static void WriteEvaluationProfile(string outputPath, int? degreeOfParallelism)
    {
        OrchardCoreRepositoryEvaluationBenchmark environment = new();
        environment.Initialize(validateEvaluationResults: false);

        try
        {
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.Delete(outputPath);
            ProfilerLogger profilerLogger = new(outputPath);
            GraphRun graphRun = ConstructGraph(environment, degreeOfParallelism, profilerLogger);
            GC.KeepAlive(graphRun.Checksum);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"The project graph evaluation profiler did not write its report: {outputPath}");
            }
        }
        finally
        {
            environment.GlobalCleanup();
        }
    }

    private void ValidateEquivalentGraphs()
    {
        GraphSnapshot degreeOne = CreateSnapshot(
            _environment,
            ConstructGraph(_environment, degreeOfParallelism: 1).Graph);
        GraphSnapshot defaultParallelism = CreateSnapshot(
            _environment,
            ConstructGraph(_environment, degreeOfParallelism: null).Graph);

        if (!degreeOne.Equals(defaultParallelism))
        {
            throw new InvalidOperationException(
                "Orchard Core project graph topology differs between degree one and default parallelism.");
        }
    }

    private static GraphRun ConstructGraph(
        OrchardCoreRepositoryEvaluationBenchmark environment,
        int? degreeOfParallelism,
        ProfilerLogger? profilerLogger = null)
    {
        using ProjectCollection collection = profilerLogger is null
            ? new(environment.GlobalProperties)
            : new(
                environment.GlobalProperties,
                [profilerLogger],
                ToolsetDefinitionLocations.Default);

        try
        {
            ProjectGraphEntryPoint[] entryPoints =
                [new(environment.SolutionPath, environment.GlobalProperties)];
            ProjectGraphOptions options = degreeOfParallelism.HasValue
                ? new ProjectGraphOptions
                {
                    DegreeOfParallelism = degreeOfParallelism.Value,
                    EntryPoints = entryPoints,
                    ProjectCollection = collection,
                }
                : new ProjectGraphOptions
                {
                    EntryPoints = entryPoints,
                    ProjectCollection = collection,
                };

            ProjectGraph graph = new(options);
            long checksum = 17;
            foreach (ProjectGraphNode node in graph.ProjectNodes)
            {
                checksum = unchecked(checksum + node.ProjectInstance.FullPath.Length);
                checksum = unchecked(checksum + node.ProjectInstance.GlobalProperties.Count);
                checksum = unchecked(checksum + node.ProjectReferences.Count);
            }

            checksum = unchecked((checksum * 31) + graph.ProjectNodes.Count);
            checksum = unchecked((checksum * 31) + graph.ConstructionMetrics.EdgeCount);
            return new(graph, options.DegreeOfParallelism, checksum);
        }
        finally
        {
            if (profilerLogger is not null)
            {
                collection.UnregisterAllLoggers();
            }
        }
    }

    private static GraphSnapshot CreateSnapshot(
        OrchardCoreRepositoryEvaluationBenchmark environment,
        ProjectGraph graph)
    {
        NodeSnapshot[] nodes = new NodeSnapshot[graph.ProjectNodes.Count];
        int nodeIndex = 0;
        foreach (ProjectGraphNode node in graph.ProjectNodes)
        {
            KeyValuePair<string, string>[] globalProperties = [.. node.ProjectInstance.GlobalProperties];
            Array.Sort(globalProperties, static (left, right) => CompareProperties(left, right));
            for (int propertyIndex = 0; propertyIndex < globalProperties.Length; propertyIndex++)
            {
                KeyValuePair<string, string> property = globalProperties[propertyIndex];
                globalProperties[propertyIndex] = new(
                    property.Key,
                    environment.Canonicalize(property.Value));
            }

            string path = environment.Canonicalize(node.ProjectInstance.FullPath);
            nodes[nodeIndex++] = new(node, path, globalProperties, CreateNodeKey(path, globalProperties));
        }

        Array.Sort(nodes, static (left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        Dictionary<ProjectGraphNode, int> nodeIndexes = new(nodes.Length);
        for (int index = 0; index < nodes.Length; index++)
        {
            nodeIndexes.Add(nodes[index].Node, index);
        }

        EdgeSnapshot[] edges = new EdgeSnapshot[graph.ConstructionMetrics.EdgeCount];
        int edgeIndex = 0;
        foreach (NodeSnapshot node in nodes)
        {
            foreach (ProjectGraphNode reference in node.Node.ProjectReferences)
            {
                edges[edgeIndex++] = new(nodeIndexes[node.Node], nodeIndexes[reference]);
            }
        }

        Array.Sort(edges, static (left, right) =>
        {
            int result = left.Source.CompareTo(right.Source);
            return result != 0
                ? result
                : left.Target.CompareTo(right.Target);
        });

        return new(nodes, edges);
    }

    private static string CreateNodeKey(
        string path,
        KeyValuePair<string, string>[] globalProperties)
    {
        string[] parts = new string[globalProperties.Length + 1];
        parts[0] = $"{path.Length}:{path}";
        for (int index = 0; index < globalProperties.Length; index++)
        {
            KeyValuePair<string, string> property = globalProperties[index];
            parts[index + 1] =
                $"{property.Key.Length}:{property.Key}{property.Value.Length}:{property.Value}";
        }

        return string.Concat(parts);
    }

    private static int CompareProperties(
        KeyValuePair<string, string> left,
        KeyValuePair<string, string> right)
    {
        int result = string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
        return result != 0
            ? result
            : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
    }

    private static void WriteMeasurementFile(
        string outputPath,
        GraphRun graphRun,
        GraphSnapshot snapshot,
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
                    writer.WriteString("workload", "ProjectGraph");
                    writer.WriteNumber("degreeOfParallelism", graphRun.DegreeOfParallelism);
                    writer.WriteNumber("elapsedTicks", elapsed.Ticks);
                    writer.WriteNumber("allocatedBytes", allocatedBytes);
                    writer.WriteNumber("checksum", graphRun.Checksum);
                    writer.WriteStartObject("topology");
                    writer.WriteStartArray("nodes");
                    foreach (NodeSnapshot node in snapshot.Nodes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", node.Path);
                        writer.WriteStartArray("globalProperties");
                        foreach (KeyValuePair<string, string> property in node.GlobalProperties)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", property.Key);
                            writer.WriteString("value", property.Value);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteStartArray("edges");
                    foreach (EdgeSnapshot edge in snapshot.Edges)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("source", edge.Source);
                        writer.WriteNumber("target", edge.Target);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
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

    private readonly record struct GraphRun(
        ProjectGraph Graph,
        int DegreeOfParallelism,
        long Checksum);

    private readonly record struct EdgeSnapshot(int Source, int Target);

    private sealed record NodeSnapshot(
        ProjectGraphNode Node,
        string Path,
        KeyValuePair<string, string>[] GlobalProperties,
        string Key);

    private sealed class GraphSnapshot(NodeSnapshot[] nodes, EdgeSnapshot[] edges) : IEquatable<GraphSnapshot>
    {
        internal NodeSnapshot[] Nodes { get; } = nodes;

        internal EdgeSnapshot[] Edges { get; } = edges;

        public bool Equals(GraphSnapshot? other)
        {
            if (other is null || Nodes.Length != other.Nodes.Length || Edges.Length != other.Edges.Length)
            {
                return false;
            }

            for (int index = 0; index < Nodes.Length; index++)
            {
                if (!string.Equals(Nodes[index].Key, other.Nodes[index].Key, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return Edges.AsSpan().SequenceEqual(other.Edges);
        }

        public override bool Equals(object? obj) => obj is GraphSnapshot other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Nodes.Length, Edges.Length);
    }
}