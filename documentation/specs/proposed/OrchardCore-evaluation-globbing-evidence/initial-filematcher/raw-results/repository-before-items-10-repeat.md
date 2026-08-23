```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-FOHWWQ : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Items  

```
| Method              | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| ItemsIsolated       | 782.0 ms | 34.46 ms | 20.51 ms |  1.01 |    0.05 | 15000.0000 | 4000.0000 | 1000.0000 | 265.33 MB |        1.01 |
| ItemsSharedSdkCache | 775.5 ms | 51.95 ms | 34.36 ms |  1.00 |    0.00 | 15000.0000 | 4000.0000 | 1000.0000 | 263.33 MB |        1.00 |
| ItemsShared         | 709.6 ms | 50.72 ms | 30.19 ms |  0.92 |    0.06 | 15000.0000 | 4000.0000 | 1000.0000 | 257.29 MB |        0.98 |
