```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host] : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  Toolchain=InProcessEmitToolchain  IterationCount=15  
LaunchCount=1  WarmupCount=6  

```
| Method            | Shape    | Mean     | Error   | StdDev  | Gen0   | Allocated |
|------------------ |--------- |---------:|--------:|--------:|-------:|----------:|
| **EvaluateCondition** | **Simple**   | **276.8 ns** | **2.13 ns** | **2.00 ns** | **0.0050** |      **96 B** |
| **EvaluateCondition** | **Compound** | **427.7 ns** | **2.23 ns** | **2.09 ns** | **0.0048** |      **96 B** |
