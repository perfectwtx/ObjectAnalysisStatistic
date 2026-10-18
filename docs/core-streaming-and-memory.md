# Core：流式处理与资源预算

本阶段把“源是否流式”“计算保留多少状态”“结果有多精确”分成可检查的契约。统计引擎、分布草图与模糊去重共享单次输入遍历；没有为报告导出保留完整记录列表。默认资源限制始终启用，即使没有设置 --max-memory。

## 执行能力

AnalysisExecutionPlan.Describe(source.Descriptor) 可以在打开输入前检查要求；运行后的 PipelineResult.ExecutionProfile 返回实际使用的配置。

| 能力 | 访问方式 | 说明 |
|---|---|---|
| JSONL / NDJSON、记录序列、数据库、CSV | 单遍读取 | CSV 与数据库驱动内部的单条分配由第三方负责；记录产出后检查准入限制 |
| 普通 JSON、XML、YAML | 缓冲输入 | 最多读取 MaxBufferedInputBytes 个解压后的字节；不宣称全过程固定内存 |
| Excel / excel-json | 需要可寻址输入 | 流无法 seek 时先有界缓冲；可 seek 文件直接使用；解析库内部另有分配 |
| 基础统计、分布草图 | 单遍、有界状态 | 频次裁剪，分位数与基数使用可合并草图；导出不触发重读 |
| 蓄水池采样 | 单遍、有界样本 | 同时限制条数和逻辑字节量；读取完总体后再分析样本 |
| 相关性 | 单遍、整行优先级蓄水池 | 所有列对齐，合并分区后仍保留全局最高随机优先级的行；矩阵 Samples 是实际保留行数 |
| 模糊去重 | 单遍、有界键集合 | 规范化键的出现次数独立于示例数；Jaccard 仅比较最多 5000 个 singleton 键 |

## 默认预算与超限行为

通过 AnalysisOptions.Memory.Resources、AnalysisAccumulator 的 resourceLimits 参数，或 SourceReadOptions.ResourceLimits 配置。AnalysisResourceLimits 是不可变 record，可以使用 with 调整；执行计划冻结整个配置，运行中修改调用方 options 不会影响正在执行的任务。

| 资源 | 默认限制 | 行为 |
|---|---|---|
| 字段 / 路径 | 512 个 / 1024 字符 | 超限失败；跨批次、跨 worker 共用字段准入集合 |
| 分析器 / 每字段值规则 | 32 个 / 32 个 | 超限失败 |
| 对象身份 | 100000 个 128 位哈希 | 超限失败，不把对象唯一数或重复组数静默改为估计；计数仍有哈希碰撞这一原有前提 |
| 重复对象示例 | 256 个，每个最多 4096 逻辑字节 | 超限省略示例，计数仍覆盖所有选中记录；示例不是全局 top duplicate 的保证 |
| 全局保留载荷 | 64 MiB | 超限失败；包含字段路径、频次身份、对象哈希与示例、规则精确身份与示例、模式模板与示例 |
| 频次 | Parse.MaxTrackedValues（默认 1000 个/字段），单值最多 16 KiB | 裁剪身份，OtherValueCount 包含所有被省略的出现次数；大值从一开始不保留原文 |
| 违规示例 | 最多 5 个/规则、每个最多 4096 逻辑字节 | 截断或省略，违规 Count 不受影响；规则 distinct 超过 128 个身份或单值过大时转 HLL |
| 预览 | 最多 100 条、合计 1 MiB | 超限停止收录；还会与宿主请求的条数取较小值 |
| 蓄水池样本 | 配置的条数、合计 64 MiB | 无法保留均匀样本时失败，不悄悄换成有偏的较短样本 |
| 输入与记录 | 缓冲输入 64 MiB；JSONL 物理行 1048576 UTF-16 字符 | 超限失败；JSONL 在组装完整行之前拒绝；malformed=skip 不能吞掉资源错误 |
| 记录准入 | 1048576 字符对应的逻辑载荷额度 | 路径、字段身份与固定开销的估算合计不得超过两倍该值（字节）；已由调用方或第三方创建的对象不能事后撤销分配 |
| 批次与并行 | 最多 1024 条、8 MiB 逻辑载荷、8 workers | 按条数或字节先到者分批；调用方请求的并行度与上限取最小值 |
| 异步桥接队列 | 128 条 | 有界 channel 背压；入队前检查单条记录限制 |
| 日期直方图 | 每字段 4096 个分钟桶 | 超限失败，包含批次合并与恢复校验 |
| 字符串模式 | 每字段 256 模板、每个输入最多 4096 字符 | 超限失败；每模板最多 3 个示例，每个截到 256 字符 |
| 相关性 | 最多 32 字段、20000 样本行 | 字段超限失败，行超限整行均匀采样；同时受 Advanced.CorrelationReservoir 约束 |
| 模糊键 | 5000 个，每个输入最多 4096 字符；独立 64 MiB 载荷预算 | 超限失败；每键最多 5 个示例，示例长度最多 256 字符，最多展示 200 组 |
| 关系路径 / 输入诊断 | 最多 1024 条 / 100 个样本诊断 | 关系超限失败；诊断截断明细并保留跳过总数 |

逻辑载荷不是 CLR 对象实际大小。值身份用 96 + CanonicalKey.Length × 2 字节计费，路径用长度 × 2 + 64，哈希项用 64，重复示例用长度 × 2；其他集合按各自条数和长度上限约束。PipelineResult.RetainedPayloadBytes 仅代表 accumulator 中上述计费项目。worker 的临时频次/规则状态由共享批次载荷、字段数、规则数、并行度共同限制；固定草图由字段数与组件数限制。采样、预览、模糊键、相关性、输入缓冲各有独立额度，不能把其中任意一个额度解释成整次任务总内存。自定义解析器、分析器和规则也必须遵守 context 中的资源契约；框架无法强行约束插件任意分配。

## soft / hard 的明确语义

- max-memory=0：不观测进程托管堆，仍执行全部资源限制。
- soft：只要启用内存监测，且调用方未指定采样，就在读取输入前选择 MemoryFallbackSampleSize（默认 50000 条，至少 2 条）的蓄水池采样；显式采样配置保留。超过堆阈值只记录压力，不中途切换策略，避免前半部分全量、后半部分采样带来的统计偏差。样本载荷等资源超限仍然失败。
- hard：读取、记录、批次和完成检查点观测到堆超限时失败，产生 ResourceLimit 终止事件；不会交付成功结果或预览。

GC.GetTotalMemory 观测的是整个进程的托管堆，多个任务相互影响；不包括 native 内存，也不是操作系统强制的 RSS 上限。第三方解析器的一次调用、投影和插件仍可能在检查点之间分配。所有“流式/有界”描述都限定于上表的输入或状态范围，不保证任意输入绝不 OOM。需要进程级硬隔离时应由宿主部署进程/容器内存限制。

## 结果与持久化

AnalysisResult.Reliability.Limitations 为频次、基数、分位数、重复/违规/模式示例、相关性、总体采样、预览和模糊去重提供统一说明。没有列出的统计在所选记录总体与各算法原有前提下保持完整；analysis-population=Sampled 表示所有统计仅描述采样总体。违规计数和省略的样本分开表达，ValueRuleResult.SamplesTruncated 会随状态持久化。

CLI、API 的 Overview/Result、Web、JSON、CSV、Markdown、HTML、Excel、PDF 与质量 JSON 传递或展示这些元数据；打码副本保留元数据。模糊报告属于启发式结果，规范化键计数覆盖选中流，但 Jaccard 不是全键穷举，具体展示也有上限。

内部快照升级为 analysis-state/v3，保存资源配置，Restore/Merge 重新执行限制。旧 v2 内部状态需要重新分析生成；外层 checkpoint 容器仍为 object-analyzer.snapshot/v3。观察/合并失败的 accumulator 被标记为不完整，不能再 Complete、Capture 或继续观察。相关性、字符串模式、Unicode 和模糊分析仍不支持持久化增量合并。

## 使用示例

~~~csharp
var options = new AnalysisOptions();
options.Memory.Resources = new AnalysisResourceLimits
{
    MaxObjectIdentities = 200_000,
    MaxRetainedPayloadBytes = 128L * 1024 * 1024,
    MaxSamplingBytes = 32L * 1024 * 1024,
    BatchSize = 512,
    MaxWorkers = 4
};
var source = DataSources.File("events.jsonl");
var plan = new AnalysisExecutionPlan(options);
var requirements = plan.Describe(source.Descriptor); // 不打开文件
var output = new AnalysisExecutor().Execute(plan,
    runContext: new AnalysisRunContext { Source = source });
~~~

本阶段没有添加几十个 CLI 资源参数；宿主代码通过上面的统一 options 契约设置额度。已有 --max-memory / --max-memory-mode / --sample-reservoir / --sample-step 继续生效，soft 的提前采样属于有意的行为变更。

## 验证结果

- 全解决方案编译：0 警告、0 错误。
- 新增 28 项边界测试全部通过，覆盖高基数、对象/字段/载荷限制、示例截断、整行采样与合并、恢复预算、同步/异步超长行、单遍高级分析、soft/hard 行为和报告出口。
- Core：750/752；剩余两项为本阶段开始前即存在的 SqliteQualityBaselineStoreTests 临时数据库清理失败（SaveGetAndDelete_Works、Save_ExistingSource_UpdatesBaseline）。
- API：125/125；Web：74/74。
