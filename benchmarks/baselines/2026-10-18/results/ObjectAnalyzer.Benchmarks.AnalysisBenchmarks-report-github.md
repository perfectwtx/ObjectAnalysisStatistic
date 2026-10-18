```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900 2.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5

```
| Method             | Records | Cardinality | Parallelism | Mean     | Error    | StdDev   | Gen0      | Gen1      | Allocated |
|------------------- |-------- |------------ |------------ |---------:|---------:|---------:|----------:|----------:|----------:|
| **ObserveAndComplete** | **10000**   | **32**          | **1**           | **231.7 ms** |  **3.52 ms** |  **2.32 ms** | **6000.0000** | **2000.0000** | **118.54 MB** |
| **ObserveAndComplete** | **10000**   | **32**          | **4**           | **182.3 ms** | **38.51 ms** | **22.91 ms** | **6000.0000** | **2000.0000** | **119.32 MB** |
| **ObserveAndComplete** | **10000**   | **4096**        | **1**           | **251.1 ms** |  **3.03 ms** |  **2.00 ms** | **6000.0000** | **2000.0000** | **120.01 MB** |
| **ObserveAndComplete** | **10000**   | **4096**        | **4**           | **188.0 ms** |  **6.63 ms** |  **3.94 ms** | **6000.0000** | **2000.0000** | **124.64 MB** |
