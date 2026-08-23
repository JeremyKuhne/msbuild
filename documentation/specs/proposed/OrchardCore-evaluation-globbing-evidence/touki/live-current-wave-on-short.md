```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 70.2 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean          | Error         | StdDev      | Ratio | RatioSD | Gen0    | Gen1    | Allocated | Alloc Ratio |
|----------------------- |--------------:|--------------:|------------:|------:|--------:|--------:|--------:|----------:|------------:|
| MSBuild                | 12,122.214 μs | 4,992.1465 μs | 273.6364 μs | 1.000 |    0.00 |  7.8125 |       - |  198.3 KB |        1.00 |
| MSBuildCacheBackedCold | 10,474.878 μs | 1,734.7098 μs |  95.0853 μs | 0.864 |    0.02 | 46.8750 | 15.6250 | 944.56 KB |        4.76 |
| MSBuildCacheBackedWarm |      1.392 μs |     0.9412 μs |   0.0516 μs | 0.000 |    0.00 |  0.3281 |  0.0057 |   6.05 KB |        0.03 |
| MsBuildEnumerator      |  7,604.029 μs | 6,266.1641 μs | 343.4696 μs | 0.627 |    0.03 |       - |       - | 158.93 KB |        0.80 |
