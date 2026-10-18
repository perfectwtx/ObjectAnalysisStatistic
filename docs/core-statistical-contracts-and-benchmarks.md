# 统计契约与性能基线

更新日期：2026-10-18。对应 Core 优化第 10 项。

## 正确性契约

新增 39 项统计、值模型、路径、跨格式和快照契约，以及 1 项 SQLite 并发临时库隔离测试，总计 40 项。测试采用固定种子和独立数学期望，不使用耗时通过阈值。

| 契约 | 覆盖与判定 |
|---|---|
| 状态合并 | 三个种子、并行度 1/4、随机分块与二叉合并树、空状态、每层 JSON 状态恢复；合并不修改右操作数。 |
| 精确统计 | 对象数、去重/重复组、字段出现/null/类型/值频次、覆盖率、溢出计数、极值和长度一致；sum/stddev 允许 1e-8、mean 允许 1e-10 的绝对舍入差。 |
| 独立数学样本 | 数值 1/3/5、显式 null、字符串和缺失；验证样本标准差、有效数值均值及计数。 |
| HLL | 固定基数 1,000/20,000，重复加入、不同合并顺序结果一致；这两组样本相对误差不超过 3%。 |
| 分位数 | 6,000 个均匀、13 点离散、带巨大间隙的样本，整体与七块逆序合并；P50/P90/P95/P99 均验证。连续/间隙样本秩误差不超过 2.5%；离散样本允许一个原子质量 1/13，且值误差不超过 1。 |
| 生成式值/路径 | 三个种子各生成 160 个路径和 160 个递归值；特殊字符、Unicode、空属性、索引、父路径/前缀、字典键与序列化往返。另测深层值、超 Int64 数字、科学计数法及数字/字符串身份。 |
| 快照兼容 | 读取冻结的 state-v3.json 并继续计算；旧版和未知状态版本明确拒绝；固定状态、检查点、Schema、分布、投影与推断模型版本标识。 |

以上草图容限是固定数据集的回归契约，不代表算法对任意分布的数学误差保证。分位数按经验分布的秩验证，重复值使用等值区间；当前插值在离散跳跃之间可能返回未出现的数值，因此另设离散容限。近似结果不要求跨合并顺序逐位相等。

共享负载 `testsupport/DeterministicRecords.cs` 同时编译进测试和基准工具，不进入生产项目。`mixed-records/v1` 包含数字、字符串、null、布尔、嵌套对象和数组；Cardinality 控制 id/group 的基数，不等于整条记录的去重数。

## 跨格式样本

固定样本位于 `tests/ObjectAnalyzer.Tests/Fixtures/Contracts`。JSON、JSONL、CSV、XML、YAML、Excel 与 SQLite 共用一份字符串语义，验证类型化字段及统计一致；数值样本在 JSON、JSONL、CSV、Excel、SQLite 间验证。

Excel 和 SQLite 输入由固定 JSON 样本在测试临时目录生成，避免二进制夹具漂移。字符串 CSV 关闭数值推断，数值 CSV 开启；XML 保留文本语义，不为通过契约而修改解析器。样本包含前导零、带逗号和引号的文本、中文以及重复记录。

## SQLite 测试隔离

`SqliteTestDatabase.Delete` 按与存储相同的规范连接字符串调用 ClearPool，只释放该临时数据库的空闲连接，再删除数据库及 WAL/SHM 文件。不调用 ClearAllPools，不更改生产池化设置，也不吞掉文件锁或权限错误。

两项原有基线存储测试和历史存储测试共用该清理逻辑。另有 8 个临时数据库并发创建、保存、读取、清理的回归测试。此前长期存在的两项文件锁失败已经消除。

## BenchmarkDotNet 工具

独立项目 `benchmarks/ObjectAnalyzer.Benchmarks` 仅引用 Core 和 BenchmarkDotNet 0.15.8，生产模块不引入性能工具依赖。

- AnalysisBenchmarks：10,000 条固定记录，id/group 基数 32/4096，并行度 1/4，每字段保留 128 个频次值，测量 Observe + Complete；输入生成在计时区外。
- CheckpointBenchmarks：同样的 10,000 条高基数记录，测量状态 JSON 序列化、反序列化、Restore 和 Complete；初始统计在计时区外。
- MemoryDiagnoser 记录每次操作的托管分配及 GC；CSV、完整 JSON 和 Markdown 保留均值、方差、置信区间和原始测量。
- 单独进程的 ResourceProbe 记录数据 SHA-256、全线程托管分配、GC 后托管保留增量和进程峰值工作集。峰值包含运行时与输入，保留增量保留 state/result，不等于业务数据净大小或硬内存预算。探针耗时不用于统计性性能比较。

工具遵循 [BenchmarkDotNet 官方运行与诊断说明](https://benchmarkdotnet.org/articles/overview)。使用 PowerShell 7 在仓库根目录运行：

```powershell
pwsh scripts/run-benchmarks.ps1 -Output artifacts/benchmarks/candidate
# 更长测量：
pwsh scripts/run-benchmarks.ps1 -Job Default -Output artifacts/benchmarks/long-run
# 仅验证工具能运行，不用于性能结论：
pwsh scripts/run-benchmarks.ps1 -Job Dry -Filter '*CheckpointBenchmarks*' -Output artifacts/benchmarks/smoke -SkipResourceProbes
```

Short 配置使用 5 次预热、10 次测量；BenchmarkDotNet 可能按默认异常值规则剔除样本，实际 N 见 JSON。每次使用新的输出目录，脚本拒绝混合已有产物；无有效统计结果会报错。元数据记录 Git 提交、工作区状态、负载与工具源代码指纹和命令参数。

## 首次实测基线

原始结果和资源探针已保存在 [2026-10-18 基线](../benchmarks/baselines/2026-10-18/README.md)。测量对应 b86a6b1 的计算内核与本阶段新增工具，Core 生产算法没有改动；这是供后续比较的起点，不是一次优化前后加速声明。

机器：Intel i9-13900，24 物理/32 逻辑核；Windows 11 25H2，.NET SDK 10.0.401、运行时 10.0.12，x64、Workstation GC。所有配置在同一台机器顺序运行。

| 场景 | 并行度 | 均值 ms | 吞吐 records/s | 分配 MiB/op | CV |
|---|---:|---:|---:|---:|---:|
| 10k，基数 32 | 1 | 231.74 | 43,152 | 118.54 | 1.0% |
| 10k，基数 32 | 4 | 182.33 | 54,844 | 119.32 | 12.6% |
| 10k，基数 4096 | 1 | 251.05 | 39,833 | 120.01 | 0.8% |
| 10k，基数 4096 | 4 | 188.03 | 53,184 | 124.64 | 2.1% |
| 检查点序列化/恢复/完成 | — | 6.72 | — | 7.47 | 0.9% |

低基数的四并行配置变异系数为 12.6%，不据此承诺稳定加速。当前分配约 118–125 MiB/10k，明显高于探针观察到的约 1.1–1.2 MiB 托管保留增量，后续可重点剖析临时转换与中间对象，不能把分配累计量误读为峰值占用。

## 比较与持续验证

```powershell
node --test scripts/compare-benchmarks.test.mjs
node scripts/compare-benchmarks.mjs benchmarks/baselines/2026-10-18 artifacts/benchmarks/candidate
```

比较脚本校验负载、工具指纹、CPU/OS/SDK/运行时/架构、测量配置与用例集合，拒绝缺失或失败的测量。它报告耗时和分配变化以及 CV，不按单次耗时自动阻断。仍需人工检查功耗、后台负载、GC/运行时调优开关及置信区间；该校验不能证明所有机器状态相同。CPU、SDK或运行时改变时应建立新环境基线，不能跨环境宣称代码加速。

普通 CI 执行全部契约、比较脚本测试和 Dry 冒烟，不设置性能门槛。Performance baseline 工作流仅手动触发，上传测量及环境产物；云端结果属于自己的机器环境，不与桌面基线直接比较。

## 版本与公开 API 策略

- 序列化版本与 NuGet/程序集版本分开管理。状态结构、组件配置或续算数学语义改变时升级状态版本，并提供明确迁移或拒绝恢复；不能从普通报告猜测续算状态。
- 当前状态为 analysis-state/v3，外层检查点 object-analyzer.snapshot/v3，Schema/分布快照为 2.0，交付投影 result-delivery/v1，推断图 schema-model/v1。
- 冻结夹具不能由测试自动重写。新版本必须保留旧夹具并说明兼容方式，或新增“明确拒绝旧版本”的测试和迁移说明。
- 包发布采用 SemVer：移除或改变公开成员、改变程序集归属、默认语义或序列化契约属于破坏性变更；新增兼容成员使用次版本，兼容修复使用补丁版本。当前仓库版本不会因加入测试自动发布或升级。
- 修改公共契约时同步检查全部宿主、状态恢复、值身份、脱敏投影和生成输出。阶段 9 的程序集拆分已经明确要求二进制调用方重新编译。

## 本次验证结果

Release 全量构建零警告、零错误；综合测试 841/841、API 132/132、Web 74/74 全部通过。比较脚本 4 项测试通过，正式运行脚本的 Dry 路径已实跑，五组统计性能测量和四个资源探针均有有效产物。
