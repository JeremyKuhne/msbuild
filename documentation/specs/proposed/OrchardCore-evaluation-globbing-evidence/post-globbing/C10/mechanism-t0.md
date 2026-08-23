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
| **EvaluateCondition** | **Simple**   | **273.6 ns** | **2.26 ns** | **2.12 ns** | **0.0093** |     **176 B** |
| **EvaluateCondition** | **Compound** | **429.6 ns** | **3.24 ns** | **3.03 ns** | **0.0091** |     **176 B** |
