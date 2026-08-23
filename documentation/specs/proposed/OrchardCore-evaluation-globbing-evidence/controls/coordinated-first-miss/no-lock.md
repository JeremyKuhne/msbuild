```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 70.16 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                        | Mean          | Error        | StdDev      | Gen0    | Gen1    | Allocated  |
|------------------------------ |--------------:|-------------:|------------:|--------:|--------:|-----------:|
| MSBuildDefaultCacheBackedCold | 12,153.331 μs | 4,002.947 μs | 219.4151 μs | 62.5000 | 15.6250 | 1290.76 KB |
| MSBuildDefaultCacheBackedWarm |      1.086 μs |     1.329 μs |   0.0729 μs |  0.3414 |  0.0057 |    6.28 KB |
