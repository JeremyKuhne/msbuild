```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-RRKBGY : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Items  

```
| Method              | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| ItemsIsolated       | 859.8 ms | 49.46 ms | 32.71 ms |  1.01 |    0.05 | 15000.0000 | 4000.0000 | 1000.0000 |  264.8 MB |        1.02 |
| ItemsSharedSdkCache | 855.6 ms | 60.95 ms | 40.31 ms |  1.00 |    0.00 | 14000.0000 | 2000.0000 |         - | 260.85 MB |        1.00 |
| ItemsShared         | 687.5 ms | 29.03 ms | 17.28 ms |  0.81 |    0.04 | 15000.0000 | 4000.0000 | 1000.0000 | 257.26 MB |        0.99 |
