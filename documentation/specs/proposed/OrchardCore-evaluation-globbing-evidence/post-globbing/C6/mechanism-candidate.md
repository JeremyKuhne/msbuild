```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host] : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  Toolchain=InProcessEmitToolchain  IterationCount=15  
LaunchCount=1  WarmupCount=6  

```
| Method                 | Shape         | Mean       | Error    | StdDev   | Gen0   | Allocated |
|----------------------- |-------------- |-----------:|---------:|---------:|-------:|----------:|
| **ExpandPropertyFunction** | **OneArgument**   |   **576.9 ns** | **12.31 ns** | **11.51 ns** | **0.0205** |     **392 B** |
| **ExpandPropertyFunction** | **TwoArguments**  |   **722.4 ns** | **20.86 ns** | **19.51 ns** | **0.0210** |     **408 B** |
| **ExpandPropertyFunction** | **FourArguments** | **4,785.2 ns** | **71.88 ns** | **67.24 ns** | **0.1068** |    **2216 B** |
| **ExpandPropertyFunction** | **QuotedComma**   | **1,236.5 ns** | **37.85 ns** | **35.40 ns** | **0.0381** |     **720 B** |
