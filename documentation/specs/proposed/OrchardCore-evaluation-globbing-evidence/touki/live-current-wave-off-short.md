```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 70.56 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean         | Error        | StdDev      | Ratio | RatioSD | Gen0    | Gen1    | Allocated | Alloc Ratio |
|----------------------- |-------------:|-------------:|------------:|------:|--------:|--------:|--------:|----------:|------------:|
| MSBuild                | 9,276.684 μs | 2,697.063 μs | 147.8351 μs | 1.000 |    0.00 | 31.2500 |  7.8125 | 574.03 KB |        1.00 |
| MSBuildCacheBackedCold | 9,719.166 μs | 1,133.417 μs |  62.1264 μs | 1.048 |    0.02 | 46.8750 | 15.6250 | 942.57 KB |        1.64 |
| MSBuildCacheBackedWarm |     1.187 μs |     2.678 μs |   0.1468 μs | 0.000 |    0.00 |  0.3281 |  0.0057 |   6.05 KB |        0.01 |
| MsBuildEnumerator      | 8,140.907 μs | 1,979.263 μs | 108.4901 μs | 0.878 |    0.02 |       - |       - | 158.93 KB |        0.28 |
