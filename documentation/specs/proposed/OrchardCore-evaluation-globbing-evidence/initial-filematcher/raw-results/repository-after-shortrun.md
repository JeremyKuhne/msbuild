```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]   : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  ShortRun : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

Job=ShortRun  PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method              | Categories | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |----------- |-----------:|-----------:|----------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| FullIsolated        | Full       |   954.5 ms | 1,949.0 ms | 106.83 ms |  1.02 |    0.13 | 16000.0000 | 4000.0000 | 1000.0000 | 282.56 MB |        1.01 |
| FullSharedSdkCache  | Full       |   939.2 ms |   680.2 ms |  37.28 ms |  1.00 |    0.00 | 16000.0000 | 4000.0000 | 1000.0000 | 280.32 MB |        1.00 |
| FullShared          | Full       |   812.7 ms |   910.6 ms |  49.91 ms |  0.87 |    0.08 | 16000.0000 | 5000.0000 | 1000.0000 | 275.89 MB |        0.98 |
|                     |            |            |            |           |       |         |            |           |           |           |             |
| ItemsIsolated       | Items      |   896.1 ms | 1,259.9 ms |  69.06 ms |  1.15 |    0.06 | 15000.0000 | 4000.0000 | 1000.0000 | 263.84 MB |        1.02 |
| ItemsSharedSdkCache | Items      |   778.0 ms |   482.9 ms |  26.47 ms |  1.00 |    0.00 | 14000.0000 | 3000.0000 |         - | 259.86 MB |        1.00 |
| ItemsShared         | Items      | 1,386.5 ms |   533.5 ms |  29.24 ms |  1.78 |    0.10 | 15000.0000 | 4000.0000 | 1000.0000 | 255.41 MB |        0.98 |
