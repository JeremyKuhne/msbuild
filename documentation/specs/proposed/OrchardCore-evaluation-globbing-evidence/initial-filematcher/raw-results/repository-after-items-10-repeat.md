```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-FDBEXK : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Items  

```
| Method              | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| ItemsIsolated       | 802.2 ms | 49.44 ms | 32.70 ms |  1.07 |    0.05 | 15000.0000 | 4000.0000 | 1000.0000 | 263.76 MB |        1.02 |
| ItemsSharedSdkCache | 752.6 ms | 24.33 ms | 16.09 ms |  1.00 |    0.00 | 15000.0000 | 4000.0000 | 1000.0000 | 259.75 MB |        1.00 |
| ItemsShared         | 681.2 ms | 19.03 ms | 12.59 ms |  0.91 |    0.03 | 15000.0000 | 4000.0000 | 1000.0000 | 255.43 MB |        0.98 |
