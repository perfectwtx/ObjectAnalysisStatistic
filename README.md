# ObjectAnalyzer

ObjectAnalyzer 是一个面向结构化与半结构化数据的**对象分析与数据质量平台**。

它可以从文件或数据库读取数据，分析字段结构、数据类型、值分布、重复数据、异常与 Schema，并进一步形成数据质量评分、质量问题、历史趋势和告警。

## 核心能力

- **对象分析**：字段数量、覆盖率、类型、唯一值、长度、数值和时间分布。
- **数据质量**：Completeness、Validity、Uniqueness、Consistency、Anomaly Control 五个维度。
- **问题中心**：统一管理必填、类型、枚举、范围、Pattern、唯一性等质量问题。
- **Schema**：Schema 快照与演进对比。
- **深度分析**：异常值、分布漂移、相关性、时间序列、字符串模式等。
- **PII 安全**：PII 识别与统一出站脱敏。
- **历史分析**：保存质量快照，支持趋势和回归分析。
- **告警**：质量阈值、质量回归、关键问题、Schema 变化和分布漂移告警。
- **Webhook**：基于 Outbox 的可靠异步投递。
- **多种入口**：CLI、HTTP API、Blazor Web。

## 数据源

支持常见 JSON、JSONL、CSV、Excel、XML、YAML 输入，并支持压缩输入以及 PostgreSQL、MySQL、SQLite、SQL Server 数据库直读。

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

### API

```bash
dotnet run --project src/ObjectAnalyzer.Api
```

### 测试

```bash
dotnet test ObjectAnalyzer.sln -c Release
```

## 数据质量模型

质量评分由五个维度组成：

| 维度 | 权重 |
|---|---:|
| Completeness | 30% |
| Validity | 30% |
| Uniqueness | 15% |
| Consistency | 15% |
| Anomaly Control | 10% |

综合分范围为 0～100。质量分用于衡量整体健康程度，具体诊断通过字段和 Issue 完成。

## 平台架构

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
  └── QualityIssue
        ↓
历史 / Dashboard / Alert / Webhook
```

CLI、API 和 Web 共用 Core 分析能力。平台层负责任务生命周期、质量历史、问题和外部通知。

## 项目结构

```text
src/
├── ObjectAnalyzer.Core/   # 核心分析引擎
├── ObjectAnalyzer/        # CLI
├── ObjectAnalyzer.Api/    # HTTP API
└── ObjectAnalyzer.Web/    # Blazor Web

tests/
├── ObjectAnalyzer.Tests/
├── ObjectAnalyzer.Api.Tests/
└── ObjectAnalyzer.Web.Tests/

docs/                      # 使用、架构与开发文档
samples/                   # 示例数据和规则
```

## 文档

- [快速开始](docs/getting-started.md)
- [总体架构](docs/architecture.md)
- [分析引擎](docs/analysis.md)
- [数据质量](docs/data-quality.md)
- [问题中心](docs/issues.md)
- [历史与趋势](docs/history.md)
- [告警与 Webhook](docs/alerts.md)
- [配置与参数](docs/configuration.md)
- [CLI](docs/cli.md)
- [API](docs/api.md)
- [安全](docs/security.md)
- [开发指南](docs/development.md)
- [路线图](docs/roadmap.md)
- [安全政策](SECURITY.md)
- [变更记录](CHANGELOG.md)

## 技术栈

- .NET 10
- C#
- ASP.NET Core
- Blazor
- SQLite
- React 前端集成

## 开源协议

请参阅仓库中的许可证文件。
