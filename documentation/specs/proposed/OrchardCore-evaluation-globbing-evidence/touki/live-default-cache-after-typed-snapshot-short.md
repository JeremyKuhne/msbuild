```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 65.17 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                        | Mean          | Error         | StdDev      | Gen0    | Gen1    | Allocated  |
|------------------------------ |--------------:|--------------:|------------:|--------:|--------:|-----------:|
| MSBuildCacheBackedCold        | 13,810.260 μs | 1,386.5644 μs |  76.0023 μs | 46.8750 | 15.6250 | 1124.32 KB |
| MSBuildDefaultCacheBackedCold |  9,952.134 μs | 4,081.7697 μs | 223.7356 μs | 46.8750 | 15.6250 | 1038.04 KB |
| MSBuildDefaultCacheBackedWarm |      1.142 μs |     0.3639 μs |   0.0199 μs |  0.3414 |  0.0057 |    6.28 KB |
| MsBuildEnumerator             |  8,942.048 μs | 7,759.9375 μs | 425.3484 μs |       - |       - |  173.67 KB |
