```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]     : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Job-RFBCOF : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=10  LaunchCount=1  
WarmupCount=5  Categories=Full  

```
| Method             | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------- |---------:|---------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| FullIsolated       | 804.3 ms | 29.28 ms | 19.37 ms |  1.00 |    0.03 | 16000.0000 | 4000.0000 | 1000.0000 | 281.61 MB |        1.00 |
| FullSharedSdkCache | 803.1 ms | 30.67 ms | 20.29 ms |  1.00 |    0.00 | 16000.0000 | 4000.0000 | 1000.0000 |  280.3 MB |        1.00 |
| FullShared         | 727.6 ms | 19.67 ms | 11.70 ms |  0.91 |    0.02 | 16000.0000 | 5000.0000 | 1000.0000 | 275.98 MB |        0.98 |
