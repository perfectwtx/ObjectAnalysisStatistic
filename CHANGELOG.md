# Changelog

本项目使用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [Unreleased]

### Changed
- **Desktop 工具壳层**：主窗口改为菜单栏 + 工具栏 + 齐平侧栏 + 状态栏；去掉大圆角导航卡片和页面留白。
- **Desktop 色板**：深色/浅色强调色从靛蓝改为钢蓝，控制台前景不再使用紫色。
- **Desktop 密度**：卡片圆角 2px、标题与表格行高收紧；分析页改为双栏数据源/规则布局。
- **Desktop 结果页**：概览 KPI 收成一条指标带；Schema / 重复 / 问题 / 作业 / 报告改为齐平工具表（页眉 + 表格）；去掉 `ui:Card` 与营销式副标题。
- **Desktop 本地化**：结果页列头、空状态与按钮改为中英资源，不再在 XAML 中写死中文。
- **界面现代化**：Web 改用 Manrope，圆角 6–14px，深色底改为近黑仪器色；侧栏改为 OA 标与内嵌选中条，去掉光晕和营销副标题。桌面深色主题与面板圆角同步。
- **Desktop 工具栏**：去掉右上角的语言和主题下拉。语言与主题仍可在工作区里切换。

### Planned
- 更丰富的数据质量规则与字段级诊断
- 更完整的质量可观测性与历史对比
- 更丰富的数据关系可视化与影响传播模型
- Docker / 发布流程持续完善

## [2.1.0] — 数据质量与关系分析平台增强

### Added
- **数据质量基线与趋势**：新增 Quality Baseline 配置、质量阈值评估、历史质量快照与趋势查询，并在 Web 中提供基线与趋势视图。
- **质量问题责任与修复闭环**：新增问题责任定位、修复记录、状态跟踪、截止时间与处理备注；成功分析后可自动验证已修复问题并更新状态。
- **对象关系分析**：分析管线在消费对象流时追踪嵌套对象与集合关系，支持递归路径、出现次数和目标字段统计，并提供 API / Web 展示。
- **Data Lineage 数据血缘**：基于对象关系生成血缘节点与父子边，提供 `/api/analysis/jobs/{jobId}/lineage` 与 Web 数据血缘页面。
- **Impact Analysis 影响分析**：基于 Schema Diff 与数据血缘计算直接 Schema 变化及父子节点传播影响，提供 `/api/analysis/jobs/{jobId}/impact` 与 Web 影响分析页面。
- **API 集成测试**：覆盖血缘、影响分析成功响应以及缺失、失败、运行中任务等边界场景。
- **Web 集成测试**：增加 Data Lineage 与 Impact Analysis 页面 HTTP 冒烟测试。
- **产品文档完善**：更新 Roadmap、Getting Started、CLI 文档，并补充当前 API / Web / Docker 能力说明。

### Fixed
- 修复对象关系、数据血缘和影响分析在 Web/API 集成过程中的命名空间、组件模型冲突及测试工厂可访问性问题。
- 修复 Web 影响分析页面在无 Schema Diff 时的空结果处理。
- 修复关系分析 API 测试缺失 ASP.NET Hosting 类型引用及内部测试存储可访问性问题。

## [2.0.0] — 架构重构（破坏性）

### Added
- **API v2 契约：五页面视图响应 + SSE 进度推送（W8）**：同步分析端点统一返回 `AnalyzeViewResponse`，按概览、预览、字段、深度分析、质量报告组织响应；异步作业支持进度快照与 SSE 事件流。
- **覆盖率拆分显示**：`FieldStatistic` 增加 `NullCount`，区分显式 null 与字段缺失。
- **统一分析管线 `AnalysisPipeline`（W1）**：解析、投影、字段选择/转换/过滤、采样与统计收敛为统一计算路径，CLI / API / Web 三宿主共用。
- **宿主端口抽象 `ObjectAnalyzer.Abstractions`**：控制台、进度、结果呈现通过宿主注入，Core 不再耦合渲染库。
- **导出注册表与门禁注册表**：导出格式和质量门禁均可通过注册实现扩展。
- **规则单轨**：统一 runtime 键来源，并支持 `maxTrackedValues`。
- **CLI `--max-tracked-values`**：控制唯一值跟踪上限，与展示数量解耦。
- **API 显式格式覆盖**：支持 `AnalysisOptions.Input.Format`。
- **PII 出站打码与审计**：API 请求级 `piiOff`、响应 PII 元数据、预览打码及结构化审计。
- **Mini API**：提供文件分析、原始文本分析、规则校验、规则参考、健康检查及 Scalar/OpenAPI 文档。
- **规则参考模板**：提供完整 JSONC 规则模板并支持注释加载。

### Changed（破坏性）
- 统一整体物化格式的采样顺序，将采样放到字段过滤之后。
- `ObjectAnalyzer.Analyze / AnalyzeDetailed` 增加可选 `maxTrackedValues` 参数。
- 清理 Core 静态可变状态，拆分 `AnalysisRunner` 阶段。
- 测试改为全量并行执行。

### Fixed
- 修复普通 JSON/JSONL/XML/YAML 的 `valueParsers` 被错误按 excel-json 处理的问题。
- 修复 filter + flatten 场景跳过字段级质量检查的问题。

### Removed
- 移除 `ObjectAnalyzer.Core` 对 Spectre.Console 的直接依赖。

