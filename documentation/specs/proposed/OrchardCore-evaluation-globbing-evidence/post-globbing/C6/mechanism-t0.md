```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host] : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

PowerPlanMode=00000000-0000-0000-0000-000000000000  Toolchain=InProcessEmitToolchain  IterationCount=15  
LaunchCount=1  WarmupCount=6  

```
| Method                 | Shape         | Mean       | Error     | StdDev    | Gen0   | Allocated |
|----------------------- |-------------- |-----------:|----------:|----------:|-------:|----------:|
| **ExpandPropertyFunction** | **OneArgument**   |   **584.9 ns** |  **11.99 ns** |  **11.22 ns** | **0.0205** |     **392 B** |
| **ExpandPropertyFunction** | **TwoArguments**  |   **735.2 ns** |  **29.69 ns** |  **27.77 ns** | **0.0248** |     **480 B** |
| **ExpandPropertyFunction** | **FourArguments** | **4,829.8 ns** | **172.62 ns** | **161.47 ns** | **0.1221** |    **2304 B** |
| **ExpandPropertyFunction** | **QuotedComma**   | **1,268.1 ns** |  **26.38 ns** |  **24.68 ns** | **0.0420** |     **792 B** |
