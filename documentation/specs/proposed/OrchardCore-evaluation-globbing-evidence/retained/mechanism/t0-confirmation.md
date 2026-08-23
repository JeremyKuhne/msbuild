```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 66.95 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15  
LaunchCount=2  WarmupCount=10  

```
| Method                        | Mean     | Error    | StdDev   | Gen0    | Gen1    | Allocated |
|------------------------------ |---------:|---------:|---------:|--------:|--------:|----------:|
| MSBuildDefaultCacheBackedCold | 11.54 ms | 0.118 ms | 0.173 ms | 93.7500 | 31.2500 |   1.71 MB |
