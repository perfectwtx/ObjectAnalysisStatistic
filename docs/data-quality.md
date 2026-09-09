# 数据质量

ObjectAnalyzer 的数据质量能力将原始分析结果转换为可比较、可追踪的质量指标。

## 五个维度

| 维度 | 权重 | 含义 |
|---|---:|---|
| Completeness | 30% | 字段数据是否完整 |
| Validity | 30% | 数据是否满足有效性约束 |
| Uniqueness | 15% | 数据或字段值的唯一程度 |
| Consistency | 15% | Schema 与数据结构是否稳定 |
| Anomaly Control | 10% | 异常值是否处于可控范围 |

综合质量分：

```text
Completeness × 30%
+ Validity × 30%
+ Uniqueness × 15%
+ Consistency × 15%
+ Anomaly Control × 10%
```

最终分数为 0 到 100。

## 字段级指标

字段质量快照包含：

- Coverage
- Null Rate
- Uniqueness
- Outlier Ratio

这些指标用于字段详情、质量趋势和问题定位。

## 质量问题

质量规则可以发现：

- 必填字段缺失
- null 率异常
- 类型或主导类型异常
- 枚举值违规
- 范围违规
- 唯一性问题
- Pattern 不匹配
- 数据集期望不满足

问题会统一转换为 `QualityIssue`，供 Issue Center、Dashboard 和告警使用。

## 质量分的使用原则

质量分用于衡量数据集整体健康程度，不代替具体问题诊断。

排查问题时，应从综合分进入维度指标，再进入字段和具体 Issue。
