# Changelog

本项目使用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [Unreleased]

### Added
- **Web：覆盖率/类型分布图表**（Chart.js，`Home.razor` + `interop.js` 渲染函数）。
- **Web：数据预览**（前 8 行 x 前 8 列，`AnalysisState.Preview`）。
- **Web：报告下载**（JSON / HTML / MD / CSV，复用 `ReportExporter.Save` + `downloadTextFile`）。
- **Web：Schema 导出下载**（C# / TypeScript / JSON Schema / SQL，SQL 支持 PostgreSQL / MySQL / SQLite / SQL Server 方言下拉，复用 `SchemaExporter.Generate*`）。
- **CLI：dotnet tool 打包**（`PackAsTool` + `ToolCommandName=objectanalyzer`，`dotnet pack` 产出 `artifacts/packages`）。
- **Docker：多阶段 Dockerfile**（SDK 10 构建，runtime 10 运行，工作目录 `/data`）。
- **CI：增加 dotnet tool pack 步骤**。
- **数据质量报告 (P1)**：`--quality <FILE>` 导出结构化 JSON；`--fail-on-violations` 在存在枚举违规时以退出码 `3` 结束，供 CI 质量门禁。
- **报告质量小节**：Markdown / HTML 报告增加“数据质量”摘要（✅/❌ + 违规率）。
- **i18n**：resx 本地化（默认中文 + `AppStrings.en.resx` 英文），顶部语言切换按钮。
- **超大文件采样分析**：`--sample-reservoir <N>`（蓄水池随机抽样，Algorithm R）与 `--sample-step <K>`（等距系统抽样），在 50GB+ 数据导出上秒级获取 ~99% 准确度的 Schema 概览；`jsonl/csv/excel/excel-json` 在行/记录级跳过未选中记录（不解析），内存占用有界（≤ N / K）。
- **SQL Schema 导出**：`--export-schema sql`（默认 PostgreSQL）`/postgres`/`mysql`/`sqlite`/`sqlserver` 生成 CREATE TABLE DDL；基础字段自动映射类型（Number → BIGINT/DOUBLE/FLOAT，DateTime → TIMESTAMP/DATETIME2，Boolean → BOOLEAN/TINYINT(1)/BIT 等），覆盖率 100% 且未出现 null 的字段生成 `NOT NULL`，嵌套对象/数组折叠为方言 JSON 列（JSONB/JSON/NVARCHAR(MAX)/TEXT），枚举规则值生成 `CHECK (col IN (...))` 约束，表名取自输出文件名。
- **数据库直连 (Live DB Ingestion)**：新增 `--connection <连接字符串>` + `--query <SQL>` 直连 PostgreSQL / MySQL / SQLite / SQL Server 分析（`--provider` 显式指定或按连接字符串关键字自动推断）。基于 `DbDataReader` 惰性逐行流式读取，无需先导出为 CSV/JSON 即可实时分析结构与质量；数据库原生值归一化为与分析管道一致的标量（数值→long/double、布尔→bool、DateTime→ISO-8601、Guid→字符串、二进制→Base64、NULL→null），并支持 `--sample-reservoir` / `--sample-step` 采样与 `-s` Schema/代码导出（表名缺省 `database`）。
- **测试**：数据质量快照回读、报告导出器快照（JSON/MD/HTML/CSV）、Web 冒烟（`WebApplicationFactory`）、i18n 单元测试、采样（SamplingTests）测试、SQL DDL（三种方言 + CHECK/可空性）测试、数据库直连（SQLite 流式读取 + 类型归一化 + 连接字符串推断）测试。

## [2026-08-10]

### Fixed
- JSON 解析剥离 UTF-8 BOM，修复流式解析回归。
- `CancelActiveAnalysis` 优化，避免文件替换/清除时误报“分析已取消”。

## [2026-08-09]

### Fixed
- Web 上传/清除竞态、报告增量写入、`TypedFileParser` 重复读取、XML 重复元素聚合、CLI Ctrl+C 取消。
- `EnumViolations` 只读封装、数组热路径优化、O(1) 分块切片、YAML 扁平化、XML 属性支持、`ConsoleReporter` 编码。
- `SchemaExporter` 反扁平化还原嵌套 DTO 与强类型数组；`EndsWith` 编译错误；计数器升级为 `long`。

### Changed
- `Hash128` 替代 MD5，流式 JSON 解析，Web 懒管道 + 取消，XML 防护。

## [2026-08-08]

### Added
- 流式解析、数值与语义类型指标（Min/Max/Avg/Sum、Email/UUID/DateTime 推断）、Schema 导出器、UX 进度。
- 重复 JSON 存储上限、`TotalDuplicateGroups` 精确统计、`[Other values]` 不占名额、`TypeLoader` 并发锁、清理死代码。
- 修复哈希碰撞/不可序列化崩溃/CSV 注入等审查问题；临时文件电路断开清理。

## [2026-08-07]

### Changed
- Web：开始分析按钮始终可见，未选文件时点击提示；显式开始分析按钮 + 缩短选项输入框宽度；移除侧边栏、粘性应用栏、可折叠配置面板、网格化选项。
- CLI：`--parallel` 校验拒绝 `-1` 以外的负值；修正 `--output` 帮助文本；Web 清除/下载增强；CSV 覆盖率小数；CI 超时。
