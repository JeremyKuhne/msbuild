// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using Microsoft.Build.Evaluation;

namespace MSBuild.Benchmarks;

[MemoryDiagnoser]
public class ExpressionShredderBenchmark
{
    public enum ExpressionShape
    {
        Simple,
        Transform,
        Multiple,
    }

    [Params(ExpressionShape.Simple, ExpressionShape.Transform, ExpressionShape.Multiple)]
    public ExpressionShape Shape { get; set; }

    private string _expression = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _expression = Shape switch
        {
            ExpressionShape.Simple => "@(Compile)",
            ExpressionShape.Transform => "@(Compile->'%(RootDir)%(Directory)%(Filename).obj', ';')",
            ExpressionShape.Multiple => "prefix;@(Compile);@(Content->'%(RelativeDir)%(Filename)%(Extension)', '|');@(ReferencePath->Distinct())",
            _ => throw new InvalidOperationException(),
        };
    }

    [Benchmark]
    public int GetReferencedItemExpressions()
    {
        ExpressionShredder.ReferencedItemExpressionsEnumerator enumerator =
            ExpressionShredder.GetReferencedItemExpressions(_expression);
        int checksum = 0;

        while (enumerator.MoveNext())
        {
            ExpressionShredder.ItemExpressionCapture capture = enumerator.Current;
            checksum = unchecked((checksum * 31) + capture.Index + capture.Length);
        }

        return checksum;
    }
}