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

## 已明确排除

- **.NET 8 LTS / 双目标评估**：按项目决策不做，保持 `net10.0` 单目标。

## 后续候选（Backlog，未排期）

- 大数据量分页/流式 Web 渲染深度优化（虚拟化表格）。
- 报告导出格式扩展（Parquet / JSONL 明细）。
- Web 端的规则文件可视化编辑与实时校验。
- 多语言覆盖 CLI 输出（当前 CLI 为中文文案）。
- 版本化发布与语义化版本自动递增（`dotnet pack` + CI 发布）。
