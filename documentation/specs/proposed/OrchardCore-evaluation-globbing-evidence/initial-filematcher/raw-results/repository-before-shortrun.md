```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.7.26360.111
  [Host]   : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2
  ShortRun : .NET 11.0.0 (11.0.26.36111), X64 RyuJIT AVX2

Job=ShortRun  PowerPlanMode=00000000-0000-0000-0000-000000000000  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method              | Categories | Mean     | Error      | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------------- |----------- |---------:|-----------:|---------:|------:|--------:|-----------:|----------:|----------:|----------:|------------:|
| FullIsolated        | Full       | 949.8 ms | 1,351.1 ms | 74.06 ms |  1.03 |    0.08 | 16000.0000 | 5000.0000 | 1000.0000 | 281.31 MB |        1.01 |
| FullSharedSdkCache  | Full       | 920.4 ms | 1,356.1 ms | 74.33 ms |  1.00 |    0.00 | 16000.0000 | 4000.0000 | 1000.0000 |  279.5 MB |        1.00 |
| FullShared          | Full       | 800.4 ms |   367.9 ms | 20.17 ms |  0.87 |    0.09 | 16000.0000 | 4000.0000 | 1000.0000 | 275.75 MB |        0.99 |
|                     |            |          |            |          |       |         |            |           |           |           |             |
| ItemsIsolated       | Items      | 841.6 ms |   404.2 ms | 22.15 ms |  1.09 |    0.01 | 15000.0000 | 4000.0000 | 1000.0000 | 264.76 MB |        1.01 |
| ItemsSharedSdkCache | Items      | 775.1 ms |   287.9 ms | 15.78 ms |  1.00 |    0.00 | 15000.0000 | 4000.0000 | 1000.0000 | 263.12 MB |        1.00 |
| ItemsShared         | Items      | 804.3 ms |   220.7 ms | 12.10 ms |  1.04 |    0.03 | 15000.0000 | 4000.0000 | 1000.0000 | 256.43 MB |        0.97 |
