```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 71.04 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean           | Error          | StdDev        | Ratio | Gen0    | Gen1    | Allocated | Alloc Ratio |
|----------------------- |---------------:|---------------:|--------------:|------:|--------:|--------:|----------:|------------:|
| MSBuild                | 8,821,862.5 ns |   484,506.4 ns |  26,557.43 ns | 1.000 | 31.2500 |  7.8125 | 574.98 KB |        1.00 |
| MSBuildCacheBackedCold | 9,172,543.8 ns | 1,869,104.1 ns | 102,451.90 ns | 1.040 | 46.8750 | 15.6250 |  943.3 KB |        1.64 |
| MSBuildCacheBackedWarm |       848.1 ns |       202.2 ns |      11.09 ns | 0.000 |  0.3281 |  0.0067 |   6.03 KB |        0.01 |
| MsBuildEnumerator      | 7,273,841.1 ns | 1,750,059.4 ns |  95,926.66 ns | 0.825 |  7.8125 |       - | 158.93 KB |        0.28 |
