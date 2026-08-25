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

## P3 — 下一版候选（待排期）

> 在 5 项分析能力缺口（日期/时间剖析、异常值检测、字段间关联、数据漂移对比、Schema 演进）中，优先落地以下 3 项；剩余 2 项（字段间关联、数据漂移对比）依赖本节产出，暂留 Backlog 后续评估。

- **日期/时间深度剖析**
  - `InternalFieldMetrics` 新增 `DateTimeStats` 通道：`RegexDateTime` 命中后尝试 `DateTimeOffset.TryParse`（支持 `Z` / `+08:00` / 无时区三种形态），落入新统计分支而不走数值/文本通道
  - `FieldStatistic` 新增 `MinDateTime` / `MaxDateTime` / `DateHistogram`（时间桶复用 `MergeableQuantileSketch` 思路，但按秒/分/时/天/周/月/年动态切档）；无时区值统一按 UTC 入桶
  - 报告（JSON / HTML / MD）新增「时间序列」小节；样本数据在 [test_data_semantic.json](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/samples/test_data_semantic.json) `createdAt` 基础上扩展

- **异常值检测（IQR / Z-Score / 3σ）**
  - 复用现有 [NumericSketch.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer/Analysis/NumericSketch.cs) `MergeableQuantileSketch` 计算 P25 / P75 → IQR（1.5×IQR 离群点）；复用 [FieldStatistic.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer/Analysis/FieldStatistic.cs) `StdDevNumber` + 均值实现 Z-Score / 3σ 离群点
  - `FieldStatistic` 新增 `OutlierCount` / `OutlierRatio` / `OutlierMethod`；流式热路径仅在 `NumberCount >= 4` 时启用，避免小样本抖动
  - `QualityCheckTypes` 新增 `OutlierRatio` 类型，规则文件支持 `maxOutlierRatio` 阈值；新增 `--fail-on-outlier-ratio` 门禁与退出码 5
  - 报告（JSON / HTML / MD）新增「异常值」小节，列出 top-N 离群值所在字段及样本

- **Schema 快照 + Diff**
  - 新增 `--export-schema-snapshot <FILE>`，输出 canonical schema JSON（字段名、推断类型、coverage、nullable、semantic、min/max/枚举值），独立于现有 `--export-schema` 的代码生成器
  - 新增 `--diff-schema <BASELINE>`，与历史快照对比，输出新增字段 / 删除字段 / 类型变化 / coverage 变化 / 语义变化
  - 报告（JSON / HTML / MD）新增「Schema 演进」小节；可作为下游「数据漂移对比」的字段级基础
  - CI 门禁可延伸：新增退出码 6（schema 破坏性变更，规则文件声明 `allowBreakingSchemaChanges: false` 时触发）

## 已明确排除

- **.NET 8 LTS / 双目标评估**：按项目决策不做，保持 `net10.0` 单目标。

## 后续候选（Backlog，未排期）

- 大数据量分页/流式 Web 渲染深度优化（虚拟化表格）。
- 报告导出格式扩展（Parquet / JSONL 明细）。
- Web 端的规则文件可视化编辑与实时校验。
- 多语言覆盖 CLI 输出（当前 CLI 为中文文案）。
- 版本化发布与语义化版本自动递增（`dotnet pack` + CI 发布）。
