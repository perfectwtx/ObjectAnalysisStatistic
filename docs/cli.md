# CLI

CLI 项目位于 `src/ObjectAnalyzer`，命令名称为 `objectanalyzer`。

## 源码运行

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

## 常用参数

```text
-i, --input                 输入数据
-o, --output                输出报告
-r, --rules                 规则文件
-m, --max-values            控制台展示的值数量
--max-tracked-values        分析阶段跟踪的唯一值上限
--where                     数据过滤表达式
--fields                    指定统计字段
--parallel                  最大并行度
--violations                导出质量违规 JSON
--quality                   导出结构化质量报告 JSON
```

## 抽样与大数据量分析

```text
--sample-reservoir N        蓄水池随机抽样最多 N 条
--sample-step N             每 N 条记录抽取 1 条
--max-memory MB             设置 .NET 托管堆监控上限
--max-memory-mode soft      超限时警告并自动启用采样
--max-memory-mode hard      超限时立即失败
```

`--sample-reservoir` 与 `--sample-step` 互斥。对于超大 JSONL 或其他流式数据，可优先使用抽样快速评估 Schema 和质量特征，再决定是否执行全量分析。

## Schema 导出与演进门禁

```text
--export-schema TYPE[:PATH]       导出 C# / TypeScript / JSON Schema / OpenAPI / Proto / SQL
--export-schema-snapshot PATH     导出 canonical Schema 快照
--diff-schema PATH                与历史 Schema 快照进行 diff
--schema-diff-report PATH         导出结构化 Schema diff JSON
--allow-breaking-schema-changes   是否允许破坏性 Schema 变更
```

例如：

```bash
objectanalyzer -i data.json --export-schema json-schema:schema.json
objectanalyzer -i data.json --export-schema-snapshot schema-current.json
objectanalyzer -i data.json --diff-schema schema-baseline.json --schema-diff-report schema-diff.json
```

`--allow-breaking-schema-changes false` 时，检测到破坏性 Schema 变更会以非零退出码结束，适合接入 CI/CD。

## 数据库直连

支持通过连接字符串和 SQL 查询直接分析数据库结果，无需先导出文件：

```text
--connection STRING      数据库连接字符串
--query SQL              SQL 查询
--provider TYPE          postgres / mysql / sqlite / sqlserver
```

例如：

```bash
objectanalyzer \
  --connection "Server=localhost;Database=demo;..." \
  --provider sqlserver \
  --query "SELECT * FROM users"
```

## JSONPath 数据提取

```text
--root-path PATH       从整体 JSON 文档提取分析子节点
--record-path PATH     对每条记录提取子节点后再分析
```

例如：

```bash
objectanalyzer -i response.json --root-path '$.data[*]'
objectanalyzer -i events.jsonl --record-path '$.payload'
```

`--root-path` 主要用于整体 JSON 文件；`--record-path` 可用于 JSON、JSONL、CSV、Excel 和数据库结果。

## 规则与合规

```text
--allow-unknown-rules       忽略规则文件中的未知键
--no-csv-number-inference   关闭 CSV 数值推断
--pii-report PATH           导出 PII 合规等级报告
--pii-off                   关闭 PII 识别与打码
--fail-on-pii-level LEVEL   PII 合规门禁
--fail-on-outlier-ratio N   数值字段异常值比例门禁
```

规则文件可用于默认值、主导类型、枚举约束以及字段值转换。PII 与异常值门禁适合在数据发布或 CI 流程中阻止不符合要求的数据继续流转。

## 增量分析与检查点

```text
--checkpoint PATH     保存分析检查点
--resume PATH         从检查点继续分析
--baseline PATH       合并历史 v3 计算状态快照
```

检查点在成功完成分析后保存，格式为 `object-analyzer.snapshot/v3`，包含计算状态而非普通报告。目前不提供运行中定期落盘。

`--resume` 根据物理行位置继续读取同一未压缩 JSONL 文件，校验已处理内容的摘要与执行配置，允许在文件末尾追加数据；空行和过滤掉的记录同样计入位置。`--baseline` 将一个独立批次与历史状态合并，不跳过当前输入。

普通 `--output` 报告以及 v1/v2 检查点均不能用于续算，需要重新分析生成 v3 状态。位置续跑暂不支持其他格式、采样、内存自动回退，以及相关性、字符串模式、Unicode 卫生和模糊去重。普通完整分析仍可使用这些能力。详见 [计算状态与快照](core-analysis-state.md)。

## 审计与自动化

```text
--audit-log PATH      输出 NDJSON 审计日志
--operator ID         指定操作者标识
--no-progress         关闭进度条，适合 CI 日志
--yes                 跳过交互式确认
--preflight-only      仅执行预检，不进行正式分析
```

审计日志记录分析生命周期、输入 SHA-256 哈希、操作者、关键选项和结果摘要，不写入字段值。`--no-progress` 可避免 CI 或脚本捕获日志中的 ANSI 控制字符。

## 质量门禁

CLI 可以将质量检查、合规检查、异常值检查和 Schema 演进检查作为门禁使用。门禁失败通过退出码反馈给 CI/CD。

常见门禁包括：

- `--fail-on-violations`：发现枚举违规时失败
- `--fail-on-pii-level`：敏感数据合规等级达到指定阈值时失败
- `--fail-on-outlier-ratio`：异常值比例超过阈值时失败
- `--allow-breaking-schema-changes false`：发现破坏性 Schema 变更时失败

## 输出

报告根据输出文件扩展名选择格式，可生成 JSON、HTML、Markdown、CSV、Excel 或 PDF 等结果。

没有指定输出文件时，可以直接查看控制台结果。

## 自动化

CLI 适合用于：

- CI 数据质量检查
- 数据文件发布前检查
- Schema 变更验证
- 数据库查询结果分析
- 批处理分析
- 本地数据探查
- 增量分析与断点续跑

完整参数以当前 CLI 帮助输出和 `docs/configuration.md` 为准。
