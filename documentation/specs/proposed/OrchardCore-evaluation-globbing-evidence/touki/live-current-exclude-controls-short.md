```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
Memory: 127.72 GB Total, 71.11 GB Available
.NET SDK 11.0.100-preview.5.26302.115
  [Host] : .NET 11.0.0 (11.0.0-preview.5.26302.115, 11.0.26.30315), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method              | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|-------------------- |----------:|----------:|----------:|------:|--------:|--------:|----------:|------------:|
| MSBuild             | 11.531 ms | 3.5966 ms | 0.1971 ms |  1.00 |    0.00 |  7.8125 |  198.3 KB |        1.00 |
| MSBuildLegacyDirect |  8.627 ms | 0.3701 ms | 0.0203 ms |  0.75 |    0.01 | 31.2500 | 573.95 KB |        2.89 |
| MSBuildReduced      | 11.163 ms | 9.4398 ms | 0.5174 ms |  0.97 |    0.04 |       - | 191.37 KB |        0.97 |
| MsBuildEnumerator   |  6.979 ms | 2.6111 ms | 0.1431 ms |  0.61 |    0.01 |  7.8125 | 158.93 KB |        0.80 |
