```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900 2.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5

```
| Method                   | Mean     | Error     | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|------------------------- |---------:|----------:|----------:|---------:|---------:|---------:|----------:|
| SerializeRestoreComplete | 6.721 ms | 0.1153 ms | 0.0603 ms | 562.5000 | 515.6250 | 296.8750 |   7.47 MB |
