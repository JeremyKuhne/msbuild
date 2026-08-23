```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host] : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  Dry    : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

Job=Dry  PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=1  
LaunchCount=1  RunStrategy=ColdStart  UnrollFactor=1  
WarmupCount=1  

```
| Method      | Mean     | Error | Gen0     | Gen1     | Allocated |
|------------ |---------:|------:|---------:|---------:|----------:|
| GetProperty | 34.74 ms |    NA | 330.0000 | 240.0000 |   6.06 MB |
