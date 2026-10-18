# 2026-10-18 首次性能基线

计算内核提交：b86a6b1；负载 mixed-records/v1。工具和测试在该提交上新增，生产计算算法未变。元数据的 SourceDirty=true 如实反映首次捕获时工具尚未提交。

BenchmarkDotNet 0.15.8；5 次预热、10 次测量（剔除异常值后的 N 见原始 JSON）；i9-13900，24 物理/32 逻辑核，Windows 11 25H2，SDK 10.0.401，运行时 10.0.12，x64 Workstation GC。

| 用例 | 均值 ms | 标准差 ms | 实际 N | 分配 MiB/op | CV |
|---|---:|---:|---:|---:|---:|
| Records=10000&Cardinality=32&Parallelism=1 | 231.737 | 2.325 | 10 | 118.54 | 1.0% |
| Records=10000&Cardinality=32&Parallelism=4 | 182.334 | 22.915 | 9 | 119.32 | 12.6% |
| Records=10000&Cardinality=4096&Parallelism=1 | 251.051 | 2.003 | 10 | 120.01 | 0.8% |
| Records=10000&Cardinality=4096&Parallelism=4 | 188.027 | 3.945 | 9 | 124.64 | 2.1% |
| SerializeRestoreComplete | 6.721 | 0.060 | 8 | 7.47 | 0.9% |

每次分析为 10,000 条记录、7 个顶层字段，保留频次上限 128。基数参数只控制 id/group。CSV 与完整 JSON 位于 results，保留环境、置信区间、原始测量和 GC 计数。低基数四并行的 CV=12.6%，不作为稳定加速结论。

资源探针在独立进程、BenchmarkDotNet 完成后顺序运行，GC 后保留 state/result；累计分配、托管保留和峰值工作集是不同口径，不能相互替代。探针耗时不作为统计性能数据。

| 基数 | 并行度 | 全线程分配 MiB | 托管保留增量 MiB | 进程峰值工作集 MiB |
|---|---:|---:|---:|---:|
| 32 | 1 | 119.12 | 1.13 | 74.83 |
| 32 | 4 | 121.47 | 1.13 | 76.17 |
| 4096 | 1 | 121.69 | 1.21 | 77.57 |
| 4096 | 4 | 125.50 | 1.21 | 77.74 |

run-metadata.json 记录负载/工具源代码指纹和捕获命令；各 probe JSON 记录实际数据 SHA-256。复现、比较和版本策略见 [统计契约与性能基线](../../../docs/core-statistical-contracts-and-benchmarks.md)。这是一份初始参考，后续不同环境须另建基线。
