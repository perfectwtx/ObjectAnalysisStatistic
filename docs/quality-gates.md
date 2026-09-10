# 质量门禁

质量门禁（Quality Gate）用于把 ObjectAnalyzer 的分析结果转换为自动化流程中的确定性通过 / 失败判断。

它与页面展示、历史趋势和告警是不同层次的能力：Gate 决定当前分析是否通过；Alert 决定是否需要通知人员。

## 适用场景

- CI/CD 中阻止低质量数据进入下游环境
- 数据发布前执行质量检查
- 定时任务发现质量回归后停止后续处理
- 对关键数据源设置最低质量标准

## 当前内置 Gate

当前内置 5 个质量门禁，并按稳定退出码顺序执行：

| Exit Code | Gate | 判断内容 |
| ---: | --- | --- |
| 3 | `QualityViolationGate` | 是否存在质量违规 |
| 4 | `PiiComplianceGate` | PII / 数据安全合规等级 |
| 5 | `OutlierRatioGate` | 数值字段异常值比例 |
| 6 | `SchemaEvolutionGate` | Schema 是否存在破坏性变更 |
| 7 | `QualityScoreGate` | 综合质量分与五维质量指标 |

Gate 按注册顺序执行。默认情况下，第一个触发的 Gate 决定 CLI 的失败退出码和失败消息；自定义 Gate 可以通过 `QualityGateRegistry.Register()` 插入到队列头部。

## Quality Score Gate

`QualityScoreGate` 支持以下阈值：

```text
FailOnOverallScoreBelow
FailOnCompletenessBelow
FailOnValidityBelow
FailOnUniquenessBelow
FailOnConsistencyBelow
FailOnAnomalyControlBelow
```

五维质量指标及权重为：

| 指标 | 权重 |
| --- | ---: |
| Completeness | 30% |
| Validity | 30% |
| Uniqueness | 15% |
| Consistency | 15% |
| AnomalyControl | 10% |

例如：

```json
{
  "gates": {
    "failOnOverallScoreBelow": 90,
    "failOnCompletenessBelow": 95,
    "failOnValidityBelow": 98
  }
}
```

阈值表示“低于该值即失败”。未设置的阈值不会参与判断。

## 配置入口

质量门禁可以通过运行时配置映射到 `AnalysisOptions`。规则配置使用：

```text
runtime.gates.failOnOverallScoreBelow
runtime.gates.failOnCompletenessBelow
runtime.gates.failOnValidityBelow
runtime.gates.failOnUniquenessBelow
runtime.gates.failOnConsistencyBelow
runtime.gates.failOnAnomalyControlBelow
```

其它内置 Gate 对应的运行参数包括：

```text
failOnViolations
failOnPiiLevel
failOnOutlierRatio
allowBreakingSchemaChanges
```

具体 CLI 参数以 `docs/cli.md` 和 `--help` 输出为准。

## Gate 与 Alert 的区别

**质量门禁**回答：

> 这一次分析是否允许继续？

**质量告警**回答：

> 这一次分析是否值得通知相关人员？

两者可以使用相同的质量指标，但生命周期不同：

```text
分析
 ↓
计算质量指标
 ↓
Quality Gate ──失败──> CLI 非零退出码 / API 结构化失败结果
 ↓
保存历史
 ↓
Alert Rule ──命中──> Webhook Delivery
```

## API 中的 Gate 结果

API 不应该重新实现一套 Gate 判断逻辑。

`AnalyzeQualityView.Gates` 使用统一的 `QualityGateEvaluation.EvaluateAll()` 计算结果，因此 API 可以同时返回：

- Gate 名称
- ExitCode
- FailureMessage
- Triggered

这样前端可以展示门禁结果，下游系统也可以直接消费结构化数据。

## 历史结果

Analysis Job 会保存实际执行时使用的 Gate 结果：

```text
AnalysisJob.GateResultsJson
```

Dashboard 读取这个历史快照，而不是使用当前配置重新计算。

这点很重要：如果今天把阈值从 90 调成 95，昨天的历史任务仍然应该保持昨天实际执行时的门禁结论。

## 执行原则

1. Gate 判断必须确定性、可重复。
2. CLI、API、Web 不重复实现阈值逻辑。
3. Gate 结果应与执行时的配置绑定。
4. 退出码保持稳定，便于 CI / 脚本集成。
5. 历史 Dashboard 使用持久化的执行结果。
6. 不建议所有数据源强制使用完全相同的阈值，应根据数据源的重要程度和历史基线设置。

## 自定义 Gate

扩展 Gate 时实现 `IQualityGate`：

```csharp
public sealed class MyQualityGate : IQualityGate
{
    public string Name => "MyQualityGate";
    public int ExitCode => 20;
    public string FailureMessage => "自定义质量门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        // 返回 GateVerdict.Pass 或 new GateVerdict(true)
    }
}
```

然后注册：

```csharp
QualityGateRegistry.Register(new MyQualityGate());
```

注册后会插入默认 Gate 队列的最前面，因此需要为自定义 Gate 设计稳定、唯一的退出码。

## 与历史基线结合

质量门禁可以与历史分析结果结合形成回归控制，例如：

```text
当前 OverallScore - 上一次 OverallScore <= -5
        ↓
质量回归
        ↓
Gate Fail 或 Alert
```

当前项目已经具备历史质量快照、质量回归页面以及 Alert Rule 能力。未来可以进一步把历史基线回归正式抽象为独立 Gate。
