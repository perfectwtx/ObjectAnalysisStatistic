# ObjectAnalyzer

ObjectAnalyzer 是一个面向结构化与半结构化数据的**对象分析与数据质量平台**。

它从文件或数据库读取数据，经过统一分析管线生成字段统计、Schema、质量指标和质量问题，并支持历史趋势、回归分析、告警与 Webhook。

## 核心能力

- **对象分析**：字段覆盖率、类型、唯一值、值分布、数值与时间统计、重复对象分析。
- **数据质量**：Completeness、Validity、Uniqueness、Consistency、Anomaly Control 五维质量评分。
- **质量问题**：统一记录必填、类型、枚举、范围、Pattern、唯一性、异常等问题。
- **Schema 分析**：Schema 快照、差异与破坏性变化识别。
- **影响分析**：结合 Schema 变化与对象血缘，识别受影响的父级对象、子级节点和相关结构，并按风险等级排序。
- **深度分析**：异常值、分布漂移、相关性、时间序列、字符串模式等。
- **对象关系与血缘**：识别嵌套对象、对象集合及其字段结构，并生成可查询的数据血缘关系。
- **历史与回归**：保存质量快照，支持质量趋势和前后回归分析。
- **告警与 Webhook**：质量阈值、质量回退、关键问题、Schema 变化、分布漂移，以及 Outbox 可靠投递。
- **安全**：PII 识别、出站脱敏、审计日志。
- **多入口**：CLI、HTTP API、Blazor Web、WPF Desktop。

## 支持的数据源

JSON、JSONL、CSV、Excel、Excel-JSON、XML、YAML，以及 gzip、bzip2、zip 压缩输入；支持 PostgreSQL、MySQL、SQLite、SQL Server 数据库直读。

## 快速开始

### CLI

```bash
dotnet restore ObjectAnalyzer.sln
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

导出报告：

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html
```

### Web

```bash
dotnet run --project src/ObjectAnalyzer.Web
```

### HTTP API

```bash
dotnet run --project src/ObjectAnalyzer.Api
```

启动后可通过 `/scalar` 使用交互式 API 文档。

### Desktop

Windows 本地工作台：

```powershell
dotnet run --project src/ObjectAnalyzer.Desktop
```

Desktop 直接复用 Core 分析能力，并在本地保存作业历史、质量快照和 JSON 报告。

完整说明见 [Desktop 使用与发布](docs/desktop.md)。

### 测试

```bash
dotnet test ObjectAnalyzer.sln -c Release
```

## 平台工作流

```text
数据源
  ↓
解析 / 转换 / 过滤 / 采样
  ↓
AnalysisPipeline
  ↓
AnalysisResult
  ↓
AnalysisJob
  ├── QualitySnapshot
  ├── FieldQualitySnapshot
  ├── QualityIssue
  └── ObjectRelationship
        ↓
 Data Lineage / Impact Analysis / Dashboard / Alert
        ↓
Webhook Outbox
        ↓
Webhook Delivery Worker
```

CLI、API 和 Web 共用 Core 分析能力。Platform 层负责任务生命周期、质量历史、问题管理和外部通知。

## 数据质量模型

| 维度 | 权重 |
|---|---:|
| Completeness | 30% |
| Validity | 30% |
| Uniqueness | 15% |
| Consistency | 15% |
| Anomaly Control | 10% |

综合分范围为 0～100。质量分用于衡量整体健康程度，具体诊断通过字段指标和 Quality Issue 完成。

## Web 页面

- `/quality`：质量总览
- `/quality/trend`：质量趋势
- `/quality/fields`：字段质量明细
- `/quality/regression`：质量回归
- `/quality/schema-impact`：Schema 影响分析
- `/quality/impact-analysis`：结合血缘的影响分析
- `/quality/relationships`：字段关系分析
- `/quality/object-relationships`：对象关系分析
- `/quality/data-lineage`：数据血缘
- `/quality/recommendations`：治理建议
- `/issues`：问题中心
- `/alert-rules`：告警规则

## 项目结构

```text
src/
├── ObjectAnalyzer.Core/           # 无第三方依赖的计算内核与契约
├── ObjectAnalyzer.Application/    # 执行管线、事件与输入端口
├── ObjectAnalyzer.Parsing/        # 文件、数据库、压缩与转换适配器
├── ObjectAnalyzer.Exporting/      # 报告、Schema 导出与规则导入
├── ObjectAnalyzer.Platform/       # 作业、质量基线、责任、修复与告警
├── ObjectAnalyzer.Infrastructure/ # SQLite、审计与密钥实现
├── ObjectAnalyzer.Hosting/        # 完整命令及模块组合
├── ObjectAnalyzer/        # CLI
├── ObjectAnalyzer.Api/    # HTTP API
├── ObjectAnalyzer.Web/    # Blazor Web
└── ObjectAnalyzer.Desktop/ # WPF Desktop

tests/
├── ObjectAnalyzer.Tests/
├── ObjectAnalyzer.Api.Tests/
└── ObjectAnalyzer.Web.Tests/

docs/                      # 产品、架构、使用与开发文档
samples/                   # 示例数据与规则
```

## 文档

| 文档 | 内容 |
|---|---|
| [快速开始](docs/getting-started.md) | 环境、启动、第一次分析 |
| [Desktop 使用与发布](docs/desktop.md) | Desktop 本地使用、数据位置、Windows 发布 |
| [总体架构](docs/architecture.md) | 模块依赖、宿主与任务生命周期 |
| [模块拆分与迁移](docs/core-module-boundaries.md) | 独立引用内核、输入端口和程序集迁移 |
| [数据质量](docs/data-quality.md) | 五维质量模型与质量指标 |
| [问题中心](docs/issues.md) | Issue 模型、来源与处理路径 |
| [历史与趋势](docs/history.md) | 快照、趋势与历史存储 |
| [告警与 Webhook](docs/alerts.md) | 告警规则、Outbox 与可靠投递 |
| [配置](docs/configuration.md) | 数据源、分析参数与规则 |
| [CLI](docs/cli.md) | 命令行使用与自动化 |
| [API](docs/api.md) | HTTP API 与 OpenAPI |
| [安全](docs/security.md) | 数据、PII、日志与 Webhook 安全 |
| [开发指南](docs/development.md) | 项目结构、开发与验证 |
| [路线图](docs/roadmap.md) | 当前阶段与后续方向 |
| [安全政策](SECURITY.md) | 仓库安全问题报告 |
| [变更记录](CHANGELOG.md) | 版本与历史变更 |

## 技术栈

- .NET 10
- C#
- ASP.NET Core
- Blazor
- WPF
- SQLite

## 设计原则

1. **单一分析事实来源**：CLI、API、Web、Desktop 共用 `AnalysisPipeline`。
2. **业务与 UI 解耦**：Core 不依赖具体宿主和展示层。
3. **标准指标结构化存储**：趋势、回归和告警优先使用标准化快照。
4. **复杂结果按需持久化**：SchemaDiff、CorrelationMatrix 等复杂结果无需强行拆表。
5. **任务可追踪**：分析任务必须能够追踪成功、失败和取消状态。
6. **可靠通知**：Webhook 使用 Outbox + Worker，避免分析完成后直接同步调用外部系统。

## Windows Desktop 发布

仓库提供 Windows x64 自包含 ZIP 发布流程：

```powershell
./scripts/publish-desktop.ps1
```

GitHub Actions 也会在 Desktop 或 Core 变更推送到 `main` 时生成 `ObjectAnalyzer-Desktop-win-x64` 构建产物。

## 开源协议

请参阅仓库中的许可证文件。
