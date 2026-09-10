# 质量门禁

质量门禁用于把 ObjectAnalyzer 的分析结果转换为自动化流程中的通过/失败判断。

## 适用场景

- CI/CD 中阻止低质量数据进入下游环境
- 定时任务中发现质量回归后停止后续处理
- 数据发布前执行一次质量检查
- 对关键数据源设置最低质量标准

## 推荐门禁指标

| 指标 | 含义 | 建议用途 |
| --- | --- | --- |
| OverallScore | 综合质量分 | 最基础的发布门禁 |
| Completeness | 完整性 | 防止字段大量缺失 |
| Validity | 有效性 | 防止规则违规值增加 |
| Uniqueness | 唯一性 | 识别重复数据 |
| Consistency | 一致性 | 关注 Schema 破坏性变化 |
| AnomalyControl | 异常控制 | 关注异常值比例 |
| IssueCount | 问题数量 | 控制问题规模 |

## 判断原则

门禁应该尽量采用确定性的数值规则，而不是依赖页面展示结果。

例如：

```text
OverallScore >= 90
Completeness >= 95
Validity >= 98
CriticalIssue = 0
BreakingSchemaChange = false
```

实际阈值应该根据数据源的重要程度和历史基线制定，不建议所有数据源使用完全相同的阈值。

## 与告警的区别

**质量门禁**回答：这一次分析是否允许继续？

**质量告警**回答：这一次分析是否值得通知相关人员？

两者可以使用相同的指标，但生命周期不同：

```text
分析
 ↓
计算质量指标
 ↓
质量门禁 ──失败──> 阻止发布/流水线失败
 ↓
质量告警 ──命中──> Webhook/通知
 ↓
保存历史
```

## 当前实现边界

当前项目已经具备质量指标、标准化问题、历史快照以及告警规则能力。CLI 和 API 可以作为自动化入口；质量门禁的统一规则执行器仍属于后续产品能力。

在实现统一 Gate Evaluator 前，不应在 CLI、Web、API 中分别复制一套阈值判断逻辑。

## 后续方向

1. 抽象 `QualityGate` / `QualityGateResult`
2. 支持 OverallScore、五维指标、IssueCount、Schema Breaking Change
3. 输出机器可读的门禁结果
4. CLI 返回稳定的非零退出码
5. API 返回结构化 gate result
6. 将门禁配置与数据源绑定
7. 支持基于历史基线的回归门禁
