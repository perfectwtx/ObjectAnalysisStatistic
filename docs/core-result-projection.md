# Core：统一结果投影与脱敏出口

第 7 阶段将计算结果和出站副本分开。统计引擎、质量计算、门禁、持久化历史与计算快照继续使用原始结果；API、Web、控制台和报告消费独立投影。没有重写或迁移数据库内的原始 ResultJson。

## 统一入口

~~~csharp
var delivery = ResultProjection.Create(rawResult, rawPreview, redact: true);
var resultForDisplay = delivery.Result;
var previewForDisplay = delivery.Preview;
var policy = delivery.Policy;
var values = ResultProjection.Values(resultForDisplay.FieldStatistics[0]);
// values: Kind / Display / Count / Redacted
~~~

- ResultProjection.Create 同时处理结果与预览，返回 AnalysisDelivery。
- ResultProjection.Redact 接受已经解析的 PiiPolicy，供兼容层和特定宿主复用。
- PiiRedactor / PreviewRedactor 保留原入口，但委托给统一投影。
- ResultProjection.Values 集中生成值展示、原始类型和出现次数。FieldStatistic.ProjectedValues 是出站视图；计算内核不使用它。不同类型的原值即使打码后显示相同，类型和计数仍单独保留；兼容的 ValueCounts 可以合并相同打码身份。
- ReadStored / Job 只投影历史读取副本，不写回存储。旧历史行也自动使用当前出口策略；损坏的历史 ResultJson 在作业展示副本中被隐藏。

## 路径与策略

PiiPolicy 使用 FieldPath 的属性段和数组下标匹配，保持大小写敏感。顶层记录的键是结构化路径，嵌套对象的键是字面属性名；例如 ["a.b"] 与 a.b 是不同路径，空属性名、引号和数组索引同样可区分。

父路径的显式策略向下继承；更具体的显式策略可以覆盖父路径策略，redact:false 可以豁免指定子树。自动推断出的子字段不能弱化显式父策略，也不能覆盖其豁免。未显式指定祖先策略时，继续使用已识别字段语义的内置打码格式。

分析结果携带 PrivacyDirectives，只包含路径、掩码格式与豁免声明。这样即使没有展平 profile，profile.token 的规则也能在预览和结构化频次中生效；不会复制整份分析规则或解密参数作为策略。

## 原始值位置与处理方式

ResultProjectionContract.Members 明确列出 AnalysisResult、FieldStatistic、规则、违规、重复对象、模糊组和模式模板等核心载体的已审计成员；ValueCarriers 列出其中可能携带原始值的位置。首次脱敏投影执行完整性校验，新增成员未审计时拒绝导出，包含 JsonInclude 的私有枚举样本字段。派生结果类型没有投影契约时同样拒绝。无需再用浅拷贝或手工罗列 AnalysisResult 的复制赋值来传递新增字段；已知结果图通过完整序列化克隆，所有可变成员与计算结果断开。

| 载体 | 出站处理 |
|---|---|
| 频次、默认值、枚举/规则/质量违规样本 | 共用结构化值投影；保持出现次数；null 保持 null |
| 预览与重复对象 JSON | 递归对象和数组，所有标量类型统一打码；敏感策略存在时，无法解析的重复内容隐藏为 [redacted] |
| 数值型、布尔型、时间型、Guid、二进制等敏感标量 | 先使用统一值模型生成不依赖文化的表示，再打码为展示字符串 |
| 模糊报告 | 携带目标 Field；对 canonical key、代表值和样例同时打码；缺少来源字段且存在敏感策略时隐藏报告 |
| 字符串模式 | 敏感字段的模板可能包含字面原值，因此隐藏模板文字；样例打码，次数和比例保留 |
| 敏感字段数值/日期摘要 | 隐藏极值、均值、分位数、数值/日期桶和异常值边界，避免从统计列还原敏感值；类型、覆盖率、违规次数等保留 |
| 敏感时间序列 | 隐藏相应字段报告，避免暴露具体时间 |
| 应用规则 | 所有脱敏投影移除转换配置和不透明的自定义 checks；敏感字段再移除默认值、枚举、正则及范围字面量；保留规则是否应用、PII 和类型等信息 |
| 自定义 AnalyzerResults | 缺少独立的脱敏结构契约，默认隐藏，包含挂在非敏感字段上的载荷 |
| 自定义违规 Message | 存在敏感策略时使用通用消息，保留 Check、Count 与近似标记；历史质量问题的展示消息也使用通用文本 |
| FilterExpression、SchemaDiff、DistributionDiff | 存在敏感策略时隐藏，避免条件字面量或基线中的原值绕过当前策略 |

上述隐藏项集中记录在 AnalysisResult.Projection.Omitted，JSON/API 可直接读取；控制台、Web、Markdown、HTML、CSV、Excel 和 PDF 显示隐藏项说明。Projection.Contract 为 result-delivery/v1。

## 幂等与兼容性

- 重复投影保持结果不变，包括类型视图和隐藏项证据。
- 自定义多字符 maskChar 按原字符长度填充，避免二次打码扩大字符串。短值至少掩掉一个字符，不再因保留前缀过长而原样透传。
- 空策略也生成独立副本，调用方不能依赖 PiiRedactor / PreviewRedactor 返回原引用；旧的浅拷贝辅助方法已移除。
- 公共 CSV/Markdown/HTML/Excel/PDF Save 默认执行投影，ReportExporter 统一路由后进入已投影渲染分支，避免重复工作。
- 显式 piiOff=true / redact=false 仍可输出原值；统一 Create 的原值结果也是独立副本。关闭打码不会恢复已经被投影删除的信息。兼容的 PiiRedactor.Redact(enabled:false) 保持原来的直通行为。
- 模糊来源和出站视图为结果的新增属性；内部计算快照仍为 analysis-state/v3，恢复后从保存的规则重新生成策略，无需数据库迁移。

## 边界

这是针对已识别或显式声明字段的出站变换，不承诺识别所有未知敏感内容。字段名称、用户自定义类别/标签等结构元数据不自动重命名。新增载体或插件需要补充契约和泄漏回归测试；未知对象型预览值明确失败，不能静默透传。未知扩展结果当前没有自定义脱敏注册协议，因此先隐藏，后续可以在审计的结构契约下扩展。

原始计算快照、Schema/分布比较基线属于用于恢复和比较的技术数据，仍保留原始语义。展示/报告不能直接把这些技术文件当作脱敏副本分发；本阶段没有替换它们，也没有删除历史数据。

## 验证结果

- 新增 Core 19 项、API 7 项测试全部通过。
- 全解决方案编译：0 警告、0 错误。
- Core：769/771；两项失败仍为原有 SqliteQualityBaselineStoreTests 临时数据库清理失败。
- API：132/132；Web：74/74。
- 历史 API 测试覆盖结果、作业详情/列表、总览、字段和 Dashboard，并在读取后重新检查持久化 JSON 完全不变。
