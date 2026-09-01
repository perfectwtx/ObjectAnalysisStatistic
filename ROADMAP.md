# Roadmap

> 目标：将 ObjectAnalyzer 从功能原型打磨为可交付的产品（CLI 工具 + Web 界面 + CI 门禁）。
> 说明：以下按产品化优先级 P0 / P1 / P2 组织；已实现项打 ✅。

## P0 — 交付与分发（已完成 ✅）

- ✅ **定位文档与 Web 对齐 CLI**：Web 界面与 CLI 共用全部核心选项（格式、扁平化、类型文件、规则、并行度、Excel-JSON 列、Schema 导出）。
- ✅ **Web 图表**：Chart.js 字段覆盖率柱状图 + Top 字段类型分布堆叠柱状图。
- ✅ **Web 数据预览**：结果区展示前 N 行 × 前 N 列的真实数据样本。
- ✅ **Web 导出**：报告下载（JSON / HTML / MD / CSV）与 Schema 下载（C# / TypeScript / JSON Schema）。
- ✅ **dotnet tool 打包**：`PackAsTool` + `ToolCommandName=objectanalyzer`，`dotnet pack` 产出 nupkg。
- ✅ **Docker**：多阶段 `Dockerfile`（构建 CLI 并在 runtime 镜像运行）。
- ✅ **CI**：GitHub Actions — build + test（含覆盖率）+ publish CLI/Web + pack tool + CodeQL。

## P1 — 数据质量与性能（已完成 ✅）

- ✅ **数据质量报告**：`--quality <FILE>` 导出结构化 JSON（Pass/Fail 摘要 + 违规率 + 逐字段明细）。
- ✅ **质量门禁**：`--fail-on-violations` 存在枚举违规时退出码 `3`，供 CI 阻断。
- ✅ **报告质量小节**：Markdown / HTML 报告内置“数据质量”摘要。
- ✅ **性能**：`Hash128` 替代 MD5、流式 JSON 解析、分块并行聚合、Web 懒管道 + 取消。
- ✅ **基准表**：README 记录真实性能数据（见 `README.md` → 性能基准）。

## P2 — 工程化与体验（已完成 ✅）

- ✅ **i18n**：resx 本地化（默认中文 + 英文），Web 顶部语言切换。
- ✅ **快照测试**：数据质量报告与各报告导出器（JSON/MD/HTML/CSV）快照回读断言。
- ✅ **Web 冒烟测试**：`WebApplicationFactory` 验证首页、404、静态资源。
- ✅ **文档**：`CHANGELOG.md` + `ROADMAP.md`。

## P3 — 下一版候选（已完成 ✅）

> 在 5 项分析能力缺口（日期/时间剖析、异常值检测、字段间关联、数据漂移对比、Schema 演进、JSONPath 投影）中，优先落地以下 4 项；剩余 2 项（字段间关联、数据漂移对比）依赖本节产出，暂留 Backlog 后续评估。

- ✅ **日期/时间深度剖析**
  - `InternalFieldMetrics` 新增 `DateTimeStats` 通道：`RegexDateTime` 命中后尝试 `DateTimeOffset.TryParse`（支持 `Z` / `+08:00` / 无时区三种形态），落入新统计分支而不走数值/文本通道
  - `FieldStatistic` 新增 `MinDateTime` / `MaxDateTime` / `DateHistogram`（时间桶复用 `MergeableQuantileSketch` 思路，但按秒/分/时/天/周/月/年动态切档）；无时区值统一按 UTC 入桶
  - 报告（JSON / HTML / MD）新增「时间序列」小节；样本数据在 [test_data_semantic.json](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/samples/test_data_semantic.json) `createdAt` 基础上扩展

- ✅ **异常值检测（IQR / Z-Score / 3σ）**
  - 复用现有 [NumericSketch.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Fields/NumericSketch.cs) `MergeableQuantileSketch` 计算 P25 / P75 → IQR（1.5×IQR 离群点）；复用 [FieldStatistic.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Fields/FieldStatistic.cs) `StdDevNumber` + 均值实现 Z-Score / 3σ 离群点
  - `FieldStatistic` 新增 `OutlierCount` / `OutlierRatio` / `OutlierMethod`；流式热路径仅在 `NumberCount >= 4` 时启用，避免小样本抖动
  - `QualityCheckTypes` 新增 `OutlierRatio` 类型，规则文件支持 `maxOutlierRatio` 阈值；新增 `--fail-on-outlier-ratio` 门禁与退出码 5
  - 报告（JSON / HTML / MD）新增「异常值」小节，列出 top-N 离群值所在字段及样本

- ✅ **Schema 快照 + Diff**
  - 新增 `--export-schema-snapshot <FILE>`，输出 canonical schema JSON（字段名、推断类型、coverage、nullable、semantic、min/max/枚举值），独立于现有 `--export-schema` 的代码生成器
  - 新增 `--diff-schema <BASELINE>`，与历史快照对比，输出新增字段 / 删除字段 / 类型变化 / coverage 变化 / 语义变化
  - 报告（JSON / HTML / MD）新增「Schema 演进」小节；可作为下游「数据漂移对比」的字段级基础
  - CI 门禁可延伸：新增退出码 6（schema 破坏性变更，规则文件声明 `allowBreakingSchemaChanges: false` 时触发）

- ✅ **JSONPath 投影（仅分析指定节点下的值）**
  - 新增 `--root-path <EXPR>` 作用于文件根（仅整体 JSON 文件生效），如 `$.data[*]` 从 `{data:[...], meta:{...}}` 提取数据数组；JSONL 等流式格式被忽略并提示
  - 新增 `--record-path <EXPR>` 作用于每条记录（JSON / JSONL / CSV / Excel / DB 行均生效，流式友好），如 `$.payload` 从每行 `{event, payload}` 只分析 payload 子对象
  - 完整支持 RFC 9535 语法（通配符 `*` / 递归下降 `..` / 过滤器 `[?(@.x=="y")]` / 多索引 `[0,2]` / 切片 `[::2]`）；通过 `Meziantou.Framework.JsonPath` 2.0 实现
  - 语法错误启动期 fail-fast（退出码 1），匹配空集时仅警告并继续；启用 `--record-path` 时自动强制不展平
  - 两者可独立使用也可组合使用（`--root-path '$.events[*]' --record-path '$.details'`）；Web UI "配置"区提供对应输入框
  - 实现见新增的 [JsonPathProjector.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Parsing/JsonPathProjector.cs)（`ApplyRootPath` / `ApplyRecordPath` / `TryValidate`）；测试 14 个用例（单元 7 + 集成 7）

## P4 — 安全合规与数据血缘（已完成 ✅）

> 国内合规场景刚需：国密算法支持 + 操作审计 + 漏洞披露渠道。

- ✅ **国密算法 SM4 / SM2 字段转换**
  - `FieldTransform.type` 新增 `sm4`（GM/T 0002-2014，对称加解密，CBC/ECB + PKCS7/None）与 `sm2`（GM/T 0003-2012，私钥解密 / 公钥验签，sm2p256v1 曲线）
  - SM2 密钥自动识别：Base64 DER (PKCS#8/X.509 SPKI)、PEM 头、hex 裸 32 字节私钥 / 64-65 字节未压缩公钥
  - 实现于 [FieldTransformProcessor.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Rules/FieldTransformProcessor.cs) `ApplySm4` / `ApplySm2` / `ApplySm2Decrypt` / `ApplySm2Verify`；密钥长度 / 模式 / 必填关系在 `ValidateTransform` 中严格校验
  - 测试 11 个用例（GM/T 0002-2014 官方测试向量 + 随机密钥 round-trip + 错误处理）

- ✅ **数据血缘与审计日志（--audit-log / --operator）**
  - 新增 `--audit-log <PATH>` 输出 NDJSON 格式事件链：`analysis_start` / `analysis_finish` / `analysis_cancel` / `analysis_error`
  - 事件字段：`runId`（串联同一运行）、`timestamp`（UTC ISO 8601）、`operator`、`toolVersion`、`inputSource`、`inputHash`（SHA-256）、`inputBytes`、`options`（关键选项快照）、`result`（仅 finish）、`exitCode`、`message`
  - **不写入任何敏感数据**：审计日志绝不记录字段值，也绝不记录 AES/SM4/SM2 的 key/iv/privateKey/publicKey/signature
  - 同步 AutoFlush + FileShare.Read，保证审计数据不丢失且支持外部聚合器并发读取
  - 审计路径不可写时仅警告，不阻塞分析主流程
  - 实现见新增 [AuditLogger.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Security/AuditLogger.cs)；测试 13 个用例（NDJSON 写盘 / 追加 / 字段省略 / 选项快照脱敏 / SHA-256 稳定 / 集成 6 例含退出码 0/3）

- ✅ **安全策略文档 SECURITY.md（中文）**
  - 支持版本矩阵、数据分类与处理原则、密钥管理建议、审计日志最佳实践
  - 漏洞披露渠道：GitHub Security Advisories（推荐）+ security@ 邮箱 + 私密 Issue
  - 响应 SLA：首次确认 3 工作日 / 严重性评估 7 工作日 / Critical-High 修复 30 自然日
  - 安全特性清单 + 致谢机制

## P5 — 分析深度增强（已完成 ✅）

> P3 的"高级分析能力"补齐到数据科学常用维度：跨字段关联、形态归纳、数据卫生、相似识别、时序分析、采样可信度、分布漂移。零侵入兼容默认分析流程——任一特性按需开启，关闭时与 v1.x 行为完全一致。

- ✅ **字段相关性矩阵（Pearson / Spearman）**
  - `FieldCorrelationCollector` 在流式阶段为每个数值字段维护蓄水池（默认 20K 行/字段），并行分片在 `MergeFrom` 时克隆 thread-local 数据后合并
  - `AnalysisResult.CorrelationMatrix` 输出完整 Pearson 矩阵 + Spearman 秩相关矩阵 + 「强相关对」速览（|r| ≥ 0.7 且样本 ≥ 30）
  - 字段数 ≥ 2 且各字段至少 3 个非空数值样本时填充；否则为 null，零开销
  - 实现见 [FieldCorrelationMatrix.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/FieldCorrelationMatrix.cs) / [FieldCorrelationCollector.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/FieldCorrelationCollector.cs)；测试含 `Correlation_TwoCorrelatedNumericFields_DetectsStrongPair` 等

- ✅ **字符串模式自动归纳**
  - `StringPatternProfiler` 将每个字符串值归一为形态模板（`###-####` / `[A-Z]{3}\d{6}` / `[a-z]+@[a-z]+\.[a-z]+` 等），按字段聚合并输出主导模板 + Top-N 模板 + 模板覆盖率 + 样本数
  - 便于规则文件 `pattern` 编写与异常形态定位
  - 实现见 [StringPatternProfiler.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/StringPatternProfiler.cs)

- ✅ **Unicode 卫生检查**
  - `UnicodeHygieneChecker` 输出 0~100 卫生评分，识别全/半角混用、零宽字符 / 双向控制符 / BOM 等不可见字符、控制字符、大小写混用、左右引号、不可见分隔符等常见脏数据来源
  - 实现见 [UnicodeHygieneChecker.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/UnicodeHygieneChecker.cs)

- ✅ **模糊去重（相似重复识别）**
  - `FuzzyDuplicateDetector` 对指定字段（如 `name` / `title` / `address`）做归一化（去空白/标点、全半角统一、大小写折叠）+ 词袋 Jaccard 相似度
  - 将"几乎一致"的对象分组为 `FuzzyDuplicateGroup`，输出组数、相似重复对、规范化键
  - 通过 `AnalysisOptions.Advanced.FuzzyDedupField` 指定目标字段；未指定时关闭，零开销
  - 实现见 [FuzzyDuplicateDetector.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/FuzzyDuplicateDetector.cs)

- ✅ **时间序列深度分析**
  - `TimeSeriesAnalyzer` 在 P3「日期/时间深度剖析」时间桶基础上做趋势识别（rising / falling / flat / mixed）、周期检测（日 / 周 / 月）、间隙分析（最大间隔 / 间隔标准差）、脉冲性（孤立 burst 数）
  - 与现有 `FieldStatistic.DateHistogram` 共用底座，无重复计算
  - 实现见 [TimeSeriesAnalyzer.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Insights/TimeSeriesAnalyzer.cs)

- ✅ **采样置信区间**
  - 启用 `--sample-reservoir` / `--sample-step` 时自动启用（`EnableSamplingConfidence` 默认 true）
  - 对覆盖率与数值均值给出 95% 置信区间，输出"有效样本量"与各字段可信度评估
  - `AnalysisOptions.Sampling.LowConfidenceWidth` 控制"低可信"判定阈值（覆盖区间宽度 ≥ 此值视为低可信，默认 0.2）
  - 实现见 [Sampling.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Sampling.cs) `ConfidenceInterval`

- ✅ **分布漂移对比（草图 vs 草图）**
  - `DistributionSnapshot` 导出每字段的 t-digest 质心 + HyperLogLog 寄存器（约 10KB/字段，JSON 可序列化、内存有界）
  - `--export-distribution-snapshot` / `--diff-distribution` / `--distribution-diff-report` 三段式：导出基线快照 → 与当前数据对比 → 结构化 diff JSON
  - `DistributionDiff` 输出均值 / 分位 / null 率 / distinct / Jaccard 多维度漂移，显著漂移字段用 σ 倍数标记
  - 与 `SchemaDiff` 互补——前者看结构、后者看分布
  - 实现见 [DistributionSnapshot.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Drift/DistributionSnapshot.cs)

- ✅ **报告（HTML / Markdown / JSON / CSV）同步**
  - HTML / Markdown 报告新增「字段相关性」「字符串模式」「Unicode 卫生」「模糊去重」「时间序列深度」「采样置信」「分布漂移」共 7 个 P4 专用小节
  - 复杂结果采用"概览 + 详情"分层（如相关性矩阵先列强相关对，再给完整 Pearson / Spearman 矩阵）
  - 实现见 [HtmlReportExporter.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Reporting/HtmlReportExporter.cs) / [MarkdownReportExporter.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Reporting/MarkdownReportExporter.cs) `WriteCorrelationSection` / `WriteStringPatternsSection` / `WriteUnicodeHygieneSection` / `WriteFuzzyDedupSection` / `WriteTimeSeriesSection` / `WriteSamplingConfidenceSection` / `WriteDistributionDiffSection`

- ✅ **Web UI 同步**
  - Blazor 页面新增 "🧬 P4 深度分析" 区域，以 7 张独立卡片（🔗 字段相关性矩阵 / 🔤 字符串模式归纳 / 🧹 Unicode 卫生检查 / 👥 模糊去重 / 📈 时间序列深度 / 🎯 采样置信 / 📉 分布漂移）展示
  - [Home.razor](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Web/Components/Pages/Home.razor) `.p4-section` / `.p4-grid` / `.p4-card` 样式（橙色系，与 P3 蓝/绿系区分）
  - [AppStrings.resx](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Web/Resources/AppStrings.resx) 同步 40+ P4 专用本地化字符串（中英文）

- ✅ **测试**
  - 18 个新增用例（`P4AnalysisDepthTests`）：模式构建、相关性识别、时间序列趋势、Unicode 卫生、模糊去重、采样置信、分布漂移等核心场景回归
  - 修复 `FieldCorrelationCollector.MergeFrom()` 流式合并时未克隆 thread-local 数据的 bug
  - 修复 `PiiRedactor.Redact()` 丢失 `CorrelationMatrix` / `FuzzyDuplicates` 等 P4 字段的脱敏克隆 bug

- ✅ **Excel / PDF 表格化报告导出**
  - **Excel（.xlsx）**：基于 MiniExcel 的多 sheet 工作簿——基础 4 sheet（摘要 / 字段统计 / 重复对象 / 违规清单）+ P4 七项分析对应的可选 sheet（字符串模式 / Unicode 卫生 / 模糊去重 / 相关性摘要 + 强相关对 + Pearson/Spearman 矩阵 / 时间序列深度 / 分布漂移摘要 + 覆盖率变化 + 数值漂移 + Distinct 漂移 / 采样置信摘要 + 字段覆盖率区间），字段统计 sheet 含完整数值统计 / 分位数 / Top 5 值 / 类型分布列
  - **PDF**：基于 QuestPDF 的 A4 结构化排版，包含同样的多章节结构（标题 / 摘要表 / 字段统计表 / 重复对象 / 违规清单 / 全部 P4 小节），自动分页 + 页码 + 表头重复
  - **CLI 路由**：`ReportExporter.Save` 检测 `-o` 文件扩展名（`.xlsx` / `.pdf`）后调用 `ExcelReportExporter.Save` / `PdfReportExporter.Save`，无需新增 CLI 选项
  - **Web 下载**：`Home.razor` 报告区新增 Excel / PDF 下载按钮；`interop.js` 新增 `downloadBinaryFile` 处理 base64 二进制流
  - **PII 打码一致**：Excel 单元格与 PDF 表格内的敏感字段值在写入前已通过 `PiiRedactor.Redact` 处理
  - 实现见新增 [ExcelReportExporter.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Reporting/ExcelReportExporter.cs) / [PdfReportExporter.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Reporting/PdfReportExporter.cs)；测试 4 个用例（`ExcelPdfReportTests`）——多 sheet / 表头列 / PDF magic number / 扩展名路由

## P6 — 鲁棒性与大规模数据可用性（已完成 ✅）

针对 P5 之后用户反馈的三类缺口：增量分析（断点续跑）、进度条/ETA、内存上限。每个子段独立可交付。P6.1 / P6.2 / P6.3 均已完成。

### P6.1 — 进度条 / ETA（已完成 ✅）
- `ObjectAnalyzer.Analyze` / `AnalyzeDetailed` 新增 `IProgress<long>?` 回调参数（位于 CancellationToken 之前，遵循 CA1068）
- `AnalyzeCore` 在分块（10K 对象）完成后通过 `progress?.Report(globalTotalObjects)` 上报累计对象数
- `AnalysisRunner.ProcessAndReport` 用 `AnsiConsole.Progress()` 替代 `AnsiConsole.Status()`，含 5 列：任务描述 / 进度条 / 百分比 / 剩余时间 (ETA) / spinner
- `AnalysisOptions.Progress.ShowProgress`（默认 true）+ CLI `--no-progress` 开关
- `EstimateTotalObjects` 辅助方法：仅在已物化列表返回 Count；不对 50GB JSONL 二次扫描数行
- 实现见 [ObjectAnalyzer.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/ObjectAnalyzer.cs) / [AnalysisRunner.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisRunner.cs) / [AnalysisOptions.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisOptions.cs)；测试 4 个用例（`ProgressReportingTests`）——回调触发次数 / 单调性 / AnalyzeDetailed 兼容性 / 开关标志

### P6.2 — 增量分析 / 断点续跑（已完成 ✅）
- `--checkpoint <PATH>`：本 run 分析完成后将「当前结果 + 已处理位置」原子写入（先 `.tmp` 再 rename，避免半写状态）。快照含 `SourcePath` / `SourceFormat` / `SourcePosition`（行号）/ `Result`，schema 版本 `object-analyzer.snapshot/v1`
- `--resume <PATH>`：加载快照，校验源文件一致性（防错把 A 文件的快照套到 B 文件上）后，解析器从 `SourcePosition` 跳过后续才读新数据；结果与快照在 `FieldStatistic` 级合并（Welford / 直方图 / HLL distinct / 枚举违规 / 时间桶）
- `--baseline <PATH>`：合并任意历史 `AnalysisResult` JSON（`--output ... .json` 产物），不需要源文件——适合「本次只分析新增窗口并与历史合并」场景
- 合并后报告自动重新覆写 `--output`，确保下游消费者看到 merge 后的最终聚合
- 限制：仅 JSONL / CSV / Excel / Excel-JSON 走行级 skip；JSON / XML / YAML 整体物化格式不支持 `--resume`（启用时给黄色警告）。数据库直连不支持行级 skip（DbDataReader 不暴露随机访问）；启用 `--resume` 配 dbMode 时直接报错
- 实现见 [AnalyzerSnapshot.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalyzerSnapshot.cs) / [AnalysisRunner.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisRunner.cs) / [AnalysisOptions.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisOptions.cs) / [FileParsers.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Parsing/FileParsers.cs)；测试 14 个用例（`AnalyzerSnapshotTests` 10 个 + `AnalysisRunnerIncrementalTests` 4 个）——原子写入 / schema 不匹配报错 / 源文件一致性校验 / 行级 skip 实际跳行 / 数值 Welford 合并精度 / 枚举违规合并 / 退出码 0

### P6.3 — `--max-memory` 内存上限（已完成 ✅）
- `--max-memory <MB>`：进程托管堆上限（仅监控 .NET 托管堆 `GC.GetTotalMemory`；不监控非托管 / native 内存）。`0` 表示不监控（默认）
- `--max-memory-mode soft|hard`（默认 soft）：
  - **soft**：超限仅打印黄色警告并自动启用 50K 蓄水池采样（仅当用户未指定 `--sample-reservoir` / `--sample-step`），让分析在有限内存下继续推进
  - **hard**：超限立即以退出码 1 结束（`MaxMemoryExceededException` → AnalysisRunner catch 转为 `analysis_error` 审计事件）
- 入口预检 + spinner/progress 回调每批 Check + 分析完成后 ThrowIfHardExceeded 三重保障——fresh 进程刚启动时 `GC.GetTotalMemory` 可能仅 0.1MB（无任何分配），仅靠入口预检不够
- 硬模式不在 progress 回调线程上抛异常（会变成"未观察异常"逃出主流程），而是设置 `HardLimitExceeded` 标记，由主流程在分析完成后调用 `ThrowIfHardExceeded` 统一抛错
- 分析结束打印「[内存] 本次分析峰值 X.X MB（--max-memory 阈值 N MB，模式 Soft/Hard）」一行总结
- 实现见 [MemoryBudget.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/MemoryBudget.cs) / [AnalysisRunner.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisRunner.cs) / [AnalysisOptions.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/AnalysisOptions.cs) / [Program.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer/Program.cs)；测试 10 个用例（`MemoryBudgetTests`）——未启用 / soft 模式 autoEnable 单次触发 / hard 模式抛异常 / peak 单调非降 / 负数拒绝 / AnalysisRunner 硬模式小阈值失败 / 充裕阈值成功 / 0 MB 禁用 / 软模式自动启用采样 / 异常消息含阈值

## P7 — 架构整理（已完成 ✅）

> 全程零行为变化（公共 API 与退出码不变），每步均通过 Release 0 警告构建 + 462 测试；逐项明细见 CHANGELOG「Unreleased / Changed」。

- ✅ **三层解耦**：核心代码（Analysis / Parsing / Reporting / TypeSystem / Security）自 CLI 项目抽出为类库 `ObjectAnalyzer.Core`；CLI 瘦身为薄宿主，Web 与测试直连 Core，解除「Web 依赖 CLI 可执行项目」的反向耦合；Dockerfile 还原缓存层同步适配。
- ✅ **一类一文件**：82 个顶级类型拆分为独立文件（文件名 = 类型名），24 个多类型聚合文件归零；`FileParsers` / `ObjectAnalyzer` / `FieldFilter` / `SchemaExporter` 等大类中的私有嵌套类型提升为顶级 `internal` 并按消费关系归位（求值助手随 `FieldFilterComparisonNode`、共享转换归 `ParserHelpers`）；`SchemaExporter` 以 partial class 按导出方言分文件。
- ✅ **目录按功能域分组**：Analysis 之下分 Fields / Insights / Drift / Quality / Rules / Filter / Schema，Reporting 分 Schema / Pii / Quality，Parsing 分 Parsers / Compression / Database，PII 与审计并入 Security 数据安全域；87 个文件纯 git mv，命名空间保持不变（避免 Web 全限定引用连锁改动）。
- ✅ **语言现代化与清理**：12 个带构造函数的类型改用 C# 12 主构造函数（InputSource 等刻意保留显式构造）；死代码清理（167 文件多余 using、2 个未使用私有成员、9 处文档腐烂、1 个闲置包引用），全量 IDE/文档诊断下 0 警告。

## 已明确排除

- **.NET 8 LTS / 双目标评估**：按项目决策不做，保持 `net10.0` 单目标。

## 后续候选（Backlog，未排期）

- 大数据量分页/流式 Web 渲染深度优化（虚拟化表格）。
- 报告导出格式扩展（Parquet / JSONL 明细）。
- Web 端的规则文件可视化编辑与实时校验。
- 多语言覆盖 CLI 输出（当前 CLI 为中文文案）。
- 版本化发布与语义化版本自动递增（`dotnet pack` + CI 发布）。
