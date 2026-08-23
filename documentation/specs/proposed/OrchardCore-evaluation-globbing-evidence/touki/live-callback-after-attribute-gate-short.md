```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 67.29 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean          | Error         | StdDev      | Gen0    | Gen1    | Allocated  |
|----------------------- |--------------:|--------------:|------------:|--------:|--------:|-----------:|
| MSBuildCacheBackedCold | 11,305.174 μs | 1,111.4975 μs |  60.9249 μs | 54.6875 | 23.4375 | 1045.55 KB |
| MSBuildCacheBackedWarm |      1.073 μs |     0.5545 μs |   0.0304 μs |  0.3357 |  0.0019 |    6.19 KB |
| MsBuildEnumerator      |  7,501.154 μs | 3,145.7378 μs | 172.4285 μs |  7.8125 |       - |  166.23 KB |
