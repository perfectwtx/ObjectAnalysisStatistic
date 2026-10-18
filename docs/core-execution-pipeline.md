# 执行管线与运行隔离

第四阶段将计算执行和宿主运行服务改为实例依赖，移除 `AnalysisServices.Current`、`AnalysisServices.Use()` 及其进程级可变状态。

## 入口与职责

| 类型 | 职责与生命周期 |
|---|---|
| `AnalysisExecutor` | 执行计算，可复用；构造时传入进度渲染端口，默认静默直接执行 |
| `AnalysisExecutionPlan` | 深拷贝已解析的配置、规则；快照不可修改，可重复或并发执行 |
| `AnalysisRunContext` | 每次运行创建一次，保存分析器注册、输入状态、游标、预览、事件订阅和输出计数 |
| 内部 `AnalysisSession` | 每次执行创建，持有计算状态、实际内存监控和惰性输入序列 |
| `AnalysisCommandRunner` | 显式接收宿主 `AnalysisServices`，负责编排预检、审计、检查点、报告和门禁 |
| `AnalysisPipeline.Execute` / `AnalysisRunner.Run` | 使用默认静默依赖的兼容适配入口，不读取进程级服务 |

调用方先通过 `RulesToOptionsConverter.Apply()` 等适配器完成规则 runtime 与显式选项的优先级合并；执行器不重新合并。命令运行器仍自行加载规则和合并 runtime，并先复制选项，避免改写调用方配置。

```csharp
var executor = new AnalysisExecutor();
var plan = new AnalysisExecutionPlan(options, rules);
var context = new AnalysisRunContext
{
    Analyzers = registry,
    EventSink = item => events.Add(item),
    PreviewLimit = 20,
    PreviewSink = rows => preview.AddRange(rows)
};
PipelineResult output = executor.Execute(plan, context, cancellationToken);
```

同一个执行器和执行计划可以并发使用，每次必须新建上下文。重复使用上下文会立即报错，不会启动第二次生命周期。调用方提供的 `InitialState`、`MemoryBudget` 仍是可变对象，须保持每次运行独占；自定义分析器工厂与共用宿主端口须满足自身的线程安全契约。

## 阶段与采样

逻辑顺序为：准备 → 输入 → 投影 → 转换 → 过滤 → 采样 → 观察 → 收尾。字段选择、转换和过滤沿用统一的 `FieldTransformProcessor`。规则编译、过滤与转换构造、JSONPath 校验、采样参数和续跑能力检查在读取数据前完成。

`StageStarted` 表示执行器进入该逻辑阶段的组装或执行边界。输入与投影、过滤等惰性阶段在观察或物化时交错执行；这些事件不代表逐条记录的阶段计时。

所有格式关闭解析器内采样，统一从投影、转换、过滤后的总体中抽样。因此 JSONL/CSV/Excel 的采样行为发生变化：过去未抽中的行可能跳过解析和规则转换，现在仍需解析并通过过滤才能参与抽样。无效的未选中行也会暴露解析错误。这换来了跨格式一致的样本语义，但可能增加解析开销。

`SamplingConfidence.EffectiveSampleSize` 使用实际分析样本数，`EstimatedTotal` 使用实际读到的过滤后总体数；覆盖率区间以样本中的字段计数计算。record-path 使用惰性 fan-out，不再预先物化全部输入。

## 事件、异常与观察器

`AnalysisEvent` 携带 `RunId`、事件种类、阶段、记录数、结构化诊断及失败分类。计算管线不再输出控制台 markup；宿主根据诊断代码和数据决定文本、颜色与界面呈现。

每个被接受的运行发出一次 `Started`，随后恰好一次 `Completed`、`Cancelled` 或 `Failed`。配置校验失败也遵循此规则。手工创建执行计划时，构造校验失败发生在运行之外，不发事件。

终止事件只覆盖计算管线。命令运行器后续的报告写入、审计或门禁仍可能返回失败退出码；管线成功不等于报告分发成功。

- `EventSink`、`ExecutionObserver`、预览回调和进度订阅同步调用，其同步异常不会改变计算结果或触发第二个终止事件。
- 上下文的 `ObserverErrors` 最多保存前 32 个通知异常，避免失败订阅者无限积累异常。
- 内部计数和内存检查使用同步 `IProgress<long>`，不会通过 `System.Progress<T>` 排队到线程池或 UI 上下文。
- 调用方若主动传入 `Progress<T>` 或自行派发异步回调，其队列、异常和完成等待由宿主负责，执行器无法捕获异步回调中的异常。
- `IProgressReporter.Run` 是执行依赖，必须同步执行工作回调一次；它自身失败会使运行失败。通知观察器不承担执行调度。

失败事件按常见异常区分 `Configuration`、`Input`、`ResourceLimit`、`Execution`，保留原始异常并原样抛出。不带本次取消请求的 `OperationCanceledException` 作为失败处理；只有本次 token 已请求取消时才发出 `Cancelled`。

## 取消与资源

取消检查覆盖输入枚举边界、JSONL 物理行（包括空行与续跑跳过的行）、根 JSON 读取、record-path 投影、并行记录及字段、模糊聚类循环和收尾。并行计算组合调用参数与 `ParallelOptions` 的 token，不改写调用方的 `ParallelOptions`。

提前退出通过 `using` / 枚举器释放链关闭输入流。测试覆盖读取中取消、并行分块内取消与解析失败，随后以独占方式重新打开文件验证句柄释放。第五阶段已接入异步数据源与读取契约，详见 [数据源与解析器契约](core-data-sources.md)。正在进行的一次同步第三方读取或解析调用仍需返回后响应取消。

内存预算按运行隔离；准备阶段可启用 soft 模式采样兜底，hard 模式在入口与进度检查点同步中止。物化分支、运行中 soft 模式预算策略及更细粒度预算仍属于第六阶段。

## 宿主迁移

旧写法 `AnalysisServices.Use(services); AnalysisRunner.Run(options)` 改为：

```csharp
var runner = new AnalysisCommandRunner(new AnalysisServices
{
    Console = console,
    Progress = progressReporter,
    Presenter = presenter
});
int exitCode = runner.Run(options, cancellationToken);
```

CLI 和 Desktop 已使用实例运行器；API 和 Web 已改为实例执行器。报告文件写入器不再读取服务或自行输出过程日志，命令运行器负责提示保存结果。摘要、合规导出与门禁的控制台依赖通过可选参数显式传入；自定义 `IQualityGate.Check` 实现需增加 `IAnalysisConsole? console = null` 参数。

本次不改变外层 v3 检查点或内部 `analysis-state/v2` 的序列化版本。既有导出格式与门禁注册表仍是应用级注册机制，其实例化不属于此次运行服务改造；运行期间不应修改这些注册表。

## 验证

新增 14 项执行器契约测试，覆盖并发隔离、配置快照、阶段顺序、通知异常、终止唯一性、取消与输入释放、失败分类和跨格式采样。原有增量、检查点、规则、报告及各宿主回归继续执行。

完整解决方案 Release 构建通过，零警告、零错误。Core 702/704 通过，剩余两项仍为原有 SQLite 临时数据库删除时的连接池文件锁失败；API 125/125、Web 74/74 全部通过。
