```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 65.56 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                        | Mean          | Error        | StdDev      | Gen0    | Gen1    | Allocated  |
|------------------------------ |--------------:|-------------:|------------:|--------:|--------:|-----------:|
| MSBuildCacheBackedCold        | 13,899.843 μs | 3,411.738 μs | 187.0089 μs | 46.8750 | 15.6250 | 1130.49 KB |
| MSBuildDefaultCacheBackedCold | 14,224.567 μs | 3,081.575 μs | 168.9115 μs | 62.5000 | 31.2500 | 1136.98 KB |
| MSBuildDefaultCacheBackedWarm |      1.196 μs |     1.499 μs |   0.0822 μs |  0.3414 |  0.0057 |    6.28 KB |
| MsBuildEnumerator             |  8,688.543 μs | 5,641.099 μs | 309.2077 μs |       - |       - |  173.67 KB |
