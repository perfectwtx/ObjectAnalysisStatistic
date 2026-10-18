# Core 可组合分析器与规则执行

第三阶段将基础字段统计与规则编译从主引擎中拆出。主引擎负责记录枚举、路径、基础计数、对象身份和并行调度；注册的组件负责对应的观察、状态合并、结果投影与持久化。

## 内置组件

| ID | 输入 | 职责 |
|---|---|---|
| `numeric` | 数字 | 有效样本数、Welford 均值/M2、分位数、直方图和异常值画像 |
| `text` | 字符串 | 长度统计和语义识别 |
| `date` | ISO 日期字符串、日期和时间戳值 | 时间范围、日期计数与分钟桶 |
| `distinct` | 所有值类型 | 完整值域的 HLL 和唯一性结果 |
| `value-rules` | 配置了逐值约束的字段 | 枚举、范围、pattern 和注册的逐值规则 |

日期组件也支持原生 DateOnly、DateTime 和 DateTimeOffset 对应的值类型。字段频次、类型计数和对象去重仍属于基础记录状态。

`FieldAnalyzerRegistry.Default` 是不可变注册集合。`With()`、`Without()`、`WithRule()` 和 `WithResultRule()` 均返回新集合，不修改默认集合，也不影响其他分析任务。

```csharp
var registry = FieldAnalyzerRegistry.Default.Without("date");
var state = new AnalysisAccumulator(analyzers: registry);
state.ObserveRange(records);
AnalysisResult result = state.Complete();
```

禁用组件后不创建其计算状态或执行其观察逻辑；启用的组件在第一次遇到适用值类型时创建状态。依赖缺失时明确拒绝配置，例如 unique 规则需要 `distinct`，异常值规则需要 `numeric`，时间序列需要 `date`。

原有静态 Analyze 入口使用默认注册。通过共享管线执行时，可在 `AnalysisRunContext.Analyzers` 注入注册集合。

## 自定义字段分析器

实现 `IFieldAnalyzer` 工厂及 `IFieldAnalyzerState` 状态。工厂声明稳定的 ID、协议版本、配置标识与适用值类型。每个字段、每个并行分区拥有独立状态。

```csharp
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Data;

sealed class NumericCounter : IFieldAnalyzer
{
    public string Id => "example.numeric-count";
    public int Version => 1;
    public IReadOnlyCollection<RecordValueKind> InputKinds { get; } =
        new[] { RecordValueKind.Number };

    public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) => new CounterState(0);
    public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data)
        => new CounterState(data.GetInt64());

    private sealed class CounterState(long initial) : IFieldAnalyzerState
    {
        private long _count = initial;
        public void Observe(RecordValue value) => _count++;
        public void Merge(IFieldAnalyzerState other) => _count += ((CounterState)other)._count;
        public JsonElement Capture() => JsonSerializer.SerializeToElement(_count);
        public void Complete(FieldStatistic result)
            => (result.AnalyzerResults ??= new())["example.numeric-count"] =
                JsonSerializer.SerializeToElement(_count);
    }
}
```

使用 `FieldAnalyzerRegistry.Default.With(new NumericCounter())` 注册。扩展输出放入 `FieldStatistic.AnalyzerResults` 的自有 ID 下，避免覆盖内置指标。

工厂需要不可变、线程安全；观察状态由状态对象持有。合并不得修改或引用源状态的可变集合，完成时应生成独立的结果，捕获时应返回有效 JSON。协议或算法语义变化要升级 Version，配置变化要体现在 ConfigurationKey 中。

## 规则执行计划

每个计算状态在读取输入前建立 `RuleExecutionPlan`，规范化字段路径、准备枚举集合、编译正则和结果级规则。完成报告时不会再次逐值编译规则。

逐值规则与收尾规则分开执行：

| 类型 | 入口 | 运行时机 |
|---|---|---|
| 枚举、范围、pattern、自定义值约束 | `IValueRuleCompiler` | 每个值被观察时 |
| required、nullRate、unique、异常值约束 | `IResultRuleCompiler` | 字段状态合并并投影后 |
| 数据集期望及自定义聚合约束 | `IResultRuleCompiler` | 最终结果阶段 |

逐值编译器返回 `CompiledValueRule`，其中接受谓词必须纯粹且线程安全。谓词异常（包括正则超时）使本次分析失败，不会被当成通过。正则执行上限为 250ms；动态正则归所属计划持有，不使用进程级无界缓存。

枚举严格按 RecordValue 身份匹配，并检查显式 null；range 只检查可以转为有限 double 的数字；pattern 使用显示文本调用 IsMatch，跳过 null，不额外添加首尾锚点。

对于从未出现但配置了 required 或 nullRate 的字段，也会生成覆盖率为零的结果并执行缺失检查。仅用于转换、默认值或类型覆盖的规则不会凭空增加结果字段。

## 注册自定义逐值规则

字段规则新增 `checks` 字典，以编译器 ID 指定配置。例如：

```json
{
  "fields": {
    "amount": { "checks": { "multipleOf": 5 } }
  }
}
```

```csharp
sealed class MultipleOfCompiler : IValueRuleCompiler
{
    public string Id => "multipleOf";
    public CompiledValueRule? Compile(FieldPath path, FieldRule definition)
    {
        if (definition.Checks?.TryGetValue(Id, out var config) != true) return null;
        int divisor = config.GetInt32();
        if (divisor <= 0) throw new ArgumentException("divisor 必须大于零");
        return new CompiledValueRule(Id, "必须为指定整数的倍数",
            value => !value.TryGetDouble(out double number) || number % divisor == 0);
    }
}
```

使用 `registry.WithRule(new MultipleOfCompiler())` 注册。未注册的 checks ID 会在读取数据前报错。自定义注册目前由调用 Core 的宿主代码提供，配置文件本身不会加载或实例化插件。

## 注册结果级规则

实现 `IResultRuleCompiler`，通过 `WithResultRule()` 注册。编译器接收规则定义的独立副本，返回 `CompiledResultRule`，使用 `ResultRuleContext` 中的总记录数、不同对象数和字段结果产生 FieldViolation。

这类规则适合数据集规模、跨字段指标及基于聚合结果的约束。返回的 Check 应使用自有名称，数据集级违规的 Field 为空字符串。结果级规则在每次 Complete 时重新评估，因此新增数据满足约束后，历史违规不会被简单追加保留。

## 计数、不同值与样本

逐值规则不依赖 ValueCounts：即使 `maxTrackedValues` 为零，违规出现次数仍然完整累计。

- `FieldViolation.Count` 与 `ValueRuleResult.Count`：精确的违规出现次数。
- `DistinctValueCount`：不同违规值数量。最多 128 个不同值时精确，超过后转为 HLL，并标记 `DistinctApproximate`。
- `Samples`：按规范身份选择的最多 5 个展示样本。
- `FieldStatistic.EnumViolationCount`：精确的枚举违规出现次数。
- `FieldStatistic.EnumViolations`：有界展示样本，不再表示完整不同值集合。

一个值同时违反多条规则时，各规则分别累计，因此汇总次数不等于去重后的违规行数。

pattern 的 Approximate 不再因频次截断而变为 true。unique 与异常值统计仍保留各自的近似语义。质量汇总使用 long 计数；报告、导出和 Web 展示均采用出现次数，而非样本列表长度。

违规样本在状态合并和恢复后继续累计。新增加的规则结果样本也接入 PII 脱敏；对于需要打码的字段，缺少脱敏结构协议的 AnalyzerResults 扩展载荷会被省略。

## 状态与兼容性

外层检查点仍为 `object-analyzer.snapshot/v3`，内部状态升级为 `analysis-state/v2`。每个字段保存组件 ID 和组件载荷，快照记录组件版本、配置以及值规则和结果规则的注册标识。

恢复自定义组件或规则时，必须显式提供匹配的注册集合：

```csharp
new AnalyzerSnapshot { State = state.Capture(), PositionKind = "batch" }
    .SaveTo("state.snapshot", registry);

var saved = AnalyzerSnapshot.LoadFrom("state.snapshot", registry);
var restored = saved.State.Restore(registry);
var report = restored.Complete();
```

缺失实现、版本变化、配置变化或缺少已观察过的组件状态均明确报错，不会退回默认实现。`AnalyzerSnapshot.Result` 的兼容投影沿用 SaveTo/LoadFrom 时显式提供的注册；未提供时使用默认注册。需要继续观察数据时，采用上述显式 Restore 方式。

旧的 `analysis-state/v1` 没有逐值规则计数与组件协议，要求重新分析，避免恢复后漏报历史违规。`FieldStateSnapshot.Metrics` 仅保留兼容诊断投影，不再作为第二份持久化数据来源。

现有相关性蓄水池、深度字符串模式、Unicode 卫生和模糊去重的恢复限制仍适用；本阶段组件化覆盖基础字段分析和规则执行，跨记录收集器后续沿相应的记录级协议扩展。

## 验证

回归覆盖自定义分析器和值规则、结果级规则、禁用组件、缺失依赖、无频次存储、完整枚举/模式计数、高基数有界样本、并行合并、组件快照恢复、版本与注册校验、缺失字段及 PII 出口。
