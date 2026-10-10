// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Filtrace.Tracing;

namespace MSBuild.ResourceTrace;

internal static class Program
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            VerifyLedger();
            return 0;
        }

        if (args.Length is < 3 or > 4 || !int.TryParse(args[1], out int pid) || pid <= 0)
        {
            Console.Error.WriteLine("Usage: ResourceTrace <trace.etl> <exact-root-pid> <new-output-directory> [matching-symbol-directory]");
            return 1;
        }

        string destination = Path.GetFullPath(args[2]);
        if (Directory.Exists(destination))
        {
            throw new IOException($"Refusing to overwrite trace evidence: {destination}");
        }

        Directory.CreateDirectory(destination);
        Console.WriteLine($"Loading exact PID {pid} and descendants; managed names use CLR events; source lookup {(args.Length == 4 ? "enabled" : "disabled")}.");
        LoadedTrace trace = new TraceLoader().Load(args[0], symbolsDirectory: args.Length == 4 ? args[3] : null,
            scope: ScopeRequest.ForProcessIds([pid], includeChildren: true));
        File.WriteAllText(Path.Combine(destination, "info.json"),
            JsonSerializer.Serialize(new { result = trace.Info, warnings = trace.Info.Warnings }, s_json));
        if (trace.Info.CpuSampling is not { TimeWeightsEstablished: true } || trace.Source.Metric.Unit != "ms")
        {
            throw new InvalidOperationException("Trace-established CPU milliseconds are required; raw counts cannot be labeled time.");
        }

        Ledger ledger = BuildLedger(trace.Source.Samples);
        if (ledger.SampleRecords != trace.Info.SampleCount || Math.Abs(ledger.TotalCpu - trace.Info.TotalWeight) > 0.001)
        {
            throw new InvalidOperationException("CPU ledger does not reconcile with filtrace's complete scoped population.");
        }

        File.WriteAllText(Path.Combine(destination, "resource-cpu.json"), JsonSerializer.Serialize(ledger, s_json));
        Console.WriteLine($"Reconciled {ledger.SampleRecords} records, {ledger.TotalCpu:N0} CPU ms; resource union {ledger.ResourcePipelineUnionCpu:N0} ms.");
        return 0;
    }

    private static Ledger BuildLedger(IReadOnlyList<SampleStack> samples)
    {
        Group[] groups =
        [
            new("lookup", @"Microsoft\.Build\..*(AssemblyResources\.GetString|PrimaryResourceCatalog[.+]|Resources\.(SR[.+]|StringResource\.)|StringResourceManager\.|IndexedStringResourceTable\.)"),
            new("resourceHelpers", @"Microsoft\.Build\..*ResourceUtilities\."),
            new("formatting", @"Microsoft\.Build\.Framework\.Utilities\.MessageFormatter\."),
            new("parsing", @"Microsoft\.Build\.Framework\.Utilities\.MessageParser\."),
            new("taskResources", @"Microsoft\.Build\..*TaskLoggingHelper(Extension)?\.(FormatResourceString|GetResourceMessage|Log(Error|Warning).*Resources)"),
            new("deferredMessages", @"Microsoft\.Build\.Framework\.LazyFormattedBuildEventArgs\.(get_Message|FormatMessageWithoutMutation)")
        ];
        Dictionary<string, int> masks = new(StringComparer.Ordinal);
        Dictionary<string, MethodCost> methods = new(StringComparer.Ordinal);
        Dictionary<string, ProcessCost> processes = new(StringComparer.Ordinal);
        HashSet<string> seenMethods = new(StringComparer.Ordinal);
        double total = 0;
        double union = 0;
        foreach (SampleStack sample in samples)
        {
            double weight = sample.Weight;
            if (!double.IsFinite(weight) || weight < 0)
            {
                throw new InvalidOperationException("Invalid CPU sample weight.");
            }

            total += weight;
            seenMethods.Clear();
            int unionMask = 0;
            int leafMask = 0;
            for (int i = 0; i < sample.Frames.Count; i++)
            {
                string frame = sample.Frames[i];
                if (!masks.TryGetValue(frame, out int mask))
                {
                    for (int group = 0; group < groups.Length; group++)
                    {
                        if (groups[group].Pattern.IsMatch(frame))
                        {
                            mask |= 1 << group;
                        }
                    }

                    masks.Add(frame, mask);
                }

                unionMask |= mask;
                leafMask = mask;
                if (mask != 0 && seenMethods.Add(frame))
                {
                    if (!methods.TryGetValue(frame, out MethodCost? method))
                    {
                        method = new(frame);
                        methods.Add(frame, method);
                    }

                    method.InclusiveCpu += weight;
                    method.ContributingRecords++;
                }
            }

            if (unionMask != 0)
            {
                union += weight;
            }

            for (int group = 0; group < groups.Length; group++)
            {
                if ((unionMask & (1 << group)) != 0)
                {
                    groups[group].InclusiveCpu += weight;
                    groups[group].ContributingRecords++;
                }

                if ((leafMask & (1 << group)) != 0)
                {
                    groups[group].SelfCpu += weight;
                }
            }

            if (leafMask != 0)
            {
                methods[sample.Frames[^1]].SelfCpu += weight;
            }

            if (!processes.TryGetValue(sample.Process, out ProcessCost? process))
            {
                process = new(sample.Process);
                processes.Add(sample.Process, process);
            }

            process.Cpu += weight;
            process.Records++;
            if (unionMask != 0)
            {
                process.ResourcePipelineUnionCpu += weight;
            }
        }

        if (samples.Count == 0 || total <= 0)
        {
            throw new InvalidOperationException("Empty or nonpositive CPU population.");
        }

        List<MethodCost> sortedMethods = [.. methods.Values];
        sortedMethods.Sort(static (left, right) => right.InclusiveCpu.CompareTo(left.InclusiveCpu));
        List<ProcessCost> sortedProcesses = [.. processes.Values];
        sortedProcesses.Sort(static (left, right) => right.Cpu.CompareTo(left.Cpu));
        return new("sampled CPU milliseconds", total, samples.Count, union, 100 * union / total,
            groups, sortedMethods, sortedProcesses,
            "Inclusive groups/methods overlap; do not add them. Union counts each stack once. Missing/inlined/unresolved frames are not proof of zero cost; CPU share is not wall-time share.");
    }

    private static void VerifyLedger()
    {
        string lookup = "microsoft.build!Microsoft.Build.Shared.AssemblyResources.GetString";
        string helper = "microsoft.build!Microsoft.Build.Shared.ResourceUtilities.FormatResourceString";
        string deferred = "microsoft.build.framework!Microsoft.Build.Framework.LazyFormattedBuildEventArgs.get_Message";
        Ledger result = BuildLedger(
        [
            new([lookup, helper, helper], 2, process: "dotnet(123)"),
            new([deferred, lookup], 3, process: "dotnet(123)"),
            new(["csc!Compiler.Work"], 5, process: "csc(456)")
        ]);
        if (result.SampleRecords != 3 || result.TotalCpu != 10 || result.ResourcePipelineUnionCpu != 5 ||
            result.Groups[0].InclusiveCpu != 5 || result.Groups[0].SelfCpu != 3 ||
            result.Groups[1].InclusiveCpu != 2 || result.Groups[1].SelfCpu != 2 ||
            result.Groups[5].InclusiveCpu != 3 || result.Methods[1].ContributingRecords != 1 ||
            result.Processes[0].Cpu + result.Processes[1].Cpu != result.TotalCpu)
        {
            throw new InvalidOperationException("CPU ledger union/overlap/recursion/process reconciliation failed.");
        }

        Ledger staticResult = BuildLedger(
        [
            new(["Microsoft.Build.Framework.Resources.StringResource.GetString",
                "Microsoft.Build.Shared.PrimaryResourceCatalog+Resources.get_BuildFinishedSuccess"], 2),
            new(["Microsoft.Build.Framework.Resources.SR+Resources.get_TaskResourceNotFound"], 3),
            new(["Compiler.Work"], 7)
        ]);
        if (staticResult.TotalCpu != 12 || staticResult.ResourcePipelineUnionCpu != 5 ||
            staticResult.Groups[0].InclusiveCpu != 5 || staticResult.Groups[0].SelfCpu != 5 ||
            staticResult.Methods.Count != 3)
        {
            throw new InvalidOperationException("CPU ledger omitted generated static accessors or resource references.");
        }

        bool rejected = false;
        try
        {
            BuildLedger([new(["frame"], double.NaN)]);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        if (!rejected)
        {
            throw new InvalidOperationException("CPU ledger accepted invalid weights.");
        }

        Console.WriteLine("PASS resource CPU ledger union, overlap, recursion, leaf, static accessors, process reconciliation and invalid-weight checks.");
    }

    private sealed record Ledger(string Unit, double TotalCpu, int SampleRecords, double ResourcePipelineUnionCpu,
        double ResourcePipelineUnionPercent, Group[] Groups, List<MethodCost> Methods, List<ProcessCost> Processes, string Interpretation);

    private sealed class Group(string name, string pattern)
    {
        public string Name { get; } = name;
        [JsonIgnore]
        public Regex Pattern { get; } = new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        public double SelfCpu { get; set; }
        public double InclusiveCpu { get; set; }
        public int ContributingRecords { get; set; }
        public string Status => ContributingRecords == 0 ? "not sampled or unresolved; not proof of zero cost" : "sampled";
    }

    private sealed class MethodCost(string name)
    {
        public string Name { get; } = name;
        public double SelfCpu { get; set; }
        public double InclusiveCpu { get; set; }
        public int ContributingRecords { get; set; }
    }

    private sealed class ProcessCost(string name)
    {
        public string Name { get; } = name;
        public double Cpu { get; set; }
        public double ResourcePipelineUnionCpu { get; set; }
        public int Records { get; set; }
    }
}
