```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 67.18 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                 | Mean      | Error    | StdDev    | Gen0    | Gen1   | Allocated |
|----------------------- |----------:|---------:|----------:|--------:|-------:|----------:|
| MSBuildLegacyDirect    | 10.224 ms | 1.782 ms | 0.0977 ms | 31.2500 | 7.8125 | 609.85 KB |
| MSBuildOptimizedDirect |  6.848 ms | 6.975 ms | 0.3823 ms |  7.8125 |      - | 202.32 KB |
| MsBuildEnumerator      |  7.661 ms | 6.812 ms | 0.3734 ms |  7.8125 |      - | 166.23 KB |
