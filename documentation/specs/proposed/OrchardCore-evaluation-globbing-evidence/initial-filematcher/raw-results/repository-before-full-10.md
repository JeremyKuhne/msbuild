```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-SZNQSX : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Full  

```
| Method             | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| FullIsolated       | 802.8 ms | 30.26 ms | 18.01 ms |  1.01 |    0.03 | 16000.0000 | 5000.0000 | 1000.0000 | 281.23 MB |        1.01 |
| FullSharedSdkCache | 792.6 ms | 25.90 ms | 17.13 ms |  1.00 |    0.00 | 16000.0000 | 4000.0000 | 1000.0000 | 279.52 MB |        1.00 |
| FullShared         | 727.1 ms | 30.39 ms | 20.10 ms |  0.92 |    0.04 | 16000.0000 | 4000.0000 | 1000.0000 | 275.75 MB |        0.99 |
