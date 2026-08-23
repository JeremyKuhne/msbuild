```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 70.84 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean      | Error     | StdDev    | Gen0    | Gen1   | Allocated |
|----------------------- |----------:|----------:|----------:|--------:|-------:|----------:|
| MSBuildLegacyDirect    |  8.768 ms | 0.8461 ms | 0.0464 ms | 31.2500 | 7.8125 | 574.04 KB |
| MSBuildOptimizedDirect | 11.377 ms | 2.6199 ms | 0.1436 ms |       - |      - |  198.3 KB |
| MsBuildEnumerator      |  7.221 ms | 1.7466 ms | 0.0957 ms |  7.8125 |      - | 158.93 KB |
