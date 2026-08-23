```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-XEIEZY : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Items  

```
| Method              | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| ItemsIsolated       | 775.9 ms | 44.45 ms | 29.40 ms |  1.03 |    0.05 | 15000.0000 | 4000.0000 | 1000.0000 |  263.8 MB |        1.02 |
| ItemsSharedSdkCache | 752.2 ms | 32.71 ms | 21.63 ms |  1.00 |    0.00 | 14000.0000 | 3000.0000 |         - | 259.86 MB |        1.00 |
| ItemsShared         | 687.3 ms | 28.11 ms | 18.60 ms |  0.91 |    0.02 | 15000.0000 | 4000.0000 | 1000.0000 | 255.45 MB |        0.98 |
