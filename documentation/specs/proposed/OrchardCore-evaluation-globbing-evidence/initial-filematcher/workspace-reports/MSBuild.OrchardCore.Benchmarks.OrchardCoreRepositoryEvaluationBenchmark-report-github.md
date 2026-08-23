```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host] : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Dry    : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

Job=Dry  PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=1  
LaunchCount=1  RunStrategy=ColdStart  UnrollFactor=1  
WarmupCount=1  Categories=Items  

```
| Method              | Mean     | Error | Ratio | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |---------:|------:|------:|-----------:|----------:|----------:|----------:|------------:|
| ItemsSharedSdkCache | 790.6 ms |    NA |  1.00 | 15000.0000 | 4000.0000 | 1000.0000 | 259.86 MB |        1.00 |
