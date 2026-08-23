```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 71.2 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method            | Mean     | Error     | StdDev   | Ratio | Gen0     | Gen1     | Allocated | Alloc Ratio |
|------------------ |---------:|----------:|---------:|------:|---------:|---------:|----------:|------------:|
| MSBuild           | 65.38 ms | 27.817 ms | 1.525 ms |  1.00 | 375.0000 | 125.0000 |    8.2 MB |        1.00 |
| MsBuildEnumerator | 13.89 ms |  1.094 ms | 0.060 ms |  0.21 | 171.8750 |  78.1250 |   3.24 MB |        0.40 |
