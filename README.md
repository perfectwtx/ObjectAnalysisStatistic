# ObjectAnalyzer

ObjectAnalyzer 是一个面向结构化与半结构化数据的**对象分析与数据质量平台**。

它从文件或数据库读取数据，经过统一分析管线生成字段统计、Schema、质量指标和质量问题，并支持历史趋势、回归分析、告警与 Webhook。

## 核心能力

- **对象分析**：字段覆盖率、类型、唯一值、值分布、数值与时间统计、重复对象分析。
- **数据质量**：Completeness、Validity、Uniqueness、Consistency、Anomaly Control 五维质量评分。
- **质量问题**：统一记录必填、类型、枚举、范围、Pattern、唯一性、异常等问题。
- **Schema 分析与导出**：独立类型推断、快照和破坏性变化识别，支持 C#、TypeScript、JSON Schema、OpenAPI 3.1、Protobuf 与 SQL。
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

需要 .NET 10 SDK；Desktop 运行需要 Windows，性能脚本需要 PowerShell 7。

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

Desktop 通过共享的应用管线与适配器调用 Core，并在本地保存作业历史、质量快照和 JSON 报告。

完整说明见 [Desktop 使用与发布](docs/desktop.md)。

### 测试

```bash
dotnet test ObjectAnalyzer.sln -c Release
```

## 独立复用计算内核

只做内存记录统计时，可以仅引用 `ObjectAnalyzer.Core`，无需数据库驱动、PDF、文件解析器或平台业务依赖：

```csharp
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Data;

var state = new AnalysisAccumulator();
state.Observe(new Dictionary<FieldPath, RecordValue>
{
    [FieldPath.Parse("amount")] = RecordValue.FromObject(10)
});
var result = state.Complete();
var schema = SchemaInference.Infer(result);
```

运行完整示例：

```bash
dotnet run --project samples/ObjectAnalyzer.MemorySample
```

需要执行事件、采样或自定义输入端口时引用 Application；使用内置文件和数据库能力时，通过 Hosting 的 `AnalysisRuntime.CreateExecutor()` 构造执行器。引用关系与破坏性变更见 [模块拆分与迁移](docs/core-module-boundaries.md)。

## 验证与性能基线

Core 十项优化已实施，覆盖统一值/路径、可恢复统计状态、可组合分析器、运行隔离、输入契约、资源预算、结果脱敏、Schema 模型和模块拆分。具体范围见 [Core 优化清单](docs/core-optimization-plan.md)。

2026-10-18 本地 Release 验证结果：构建零警告、零错误；综合测试 **841/841**、API **132/132**、Web **74/74** 全部通过，原有 SQLite 临时文件锁失败已修复。

独立 BenchmarkDotNet 项目记录固定负载的吞吐、托管分配、GC、检查点恢复及资源探针结果。首次五组实测保存在 [2026-10-18 基线](benchmarks/baselines/2026-10-18/README.md)。运行与比较：

```powershell
pwsh scripts/run-benchmarks.ps1 -Output artifacts/benchmarks/candidate
node scripts/compare-benchmarks.mjs benchmarks/baselines/2026-10-18 artifacts/benchmarks/candidate
```

比较脚本需要 Node.js；使用新的输出目录，并在相同机器、运行时、负载和测量配置下比较。CI 执行契约测试和基准冒烟，不设置耗时通过阈值；完整性能测量可通过 GitHub Actions 的 Performance baseline 工作流手动触发。容限、环境与版本策略见 [统计契约与性能基线](docs/core-statistical-contracts-and-benchmarks.md)。

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

CLI、API、Web 和 Desktop 共用 Application 执行管线与 Core 计算内核。Platform 定义任务生命周期、质量历史、问题管理和告警业务，Infrastructure 提供 SQLite 等实现，Hosting 与各宿主负责组合。

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
├── ObjectAnalyzer/                # CLI
├── ObjectAnalyzer.Api/            # HTTP API
├── ObjectAnalyzer.Web/            # Blazor Web
└── ObjectAnalyzer.Desktop/        # WPF Desktop

tests/
├── ObjectAnalyzer.Tests/
├── ObjectAnalyzer.Api.Tests/
└── ObjectAnalyzer.Web.Tests/

benchmarks/                # BenchmarkDotNet 工具与实测基线
testsupport/               # 契约测试与基准共享的固定负载
docs/                      # 产品、架构、使用与开发文档
samples/                   # 示例数据、规则与独立内核应用
```

## 文档

| 文档 | 内容 |
|---|---|
| [快速开始](docs/getting-started.md) | 环境、启动、第一次分析 |
| [Desktop 使用与发布](docs/desktop.md) | Desktop 本地使用、数据位置、Windows 发布 |
| [总体架构](docs/architecture.md) | 模块依赖、宿主与任务生命周期 |
| [模块拆分与迁移](docs/core-module-boundaries.md) | 独立引用内核、输入端口和程序集迁移 |
| [Core 优化清单](docs/core-optimization-plan.md) | 十项重构的状态、范围与验收依据 |
| [Schema 推断与导出](docs/core-schema-model.md) | 类型图、命名、导出后端与降级诊断 |
| [统计契约与性能基线](docs/core-statistical-contracts-and-benchmarks.md) | 合并性质、跨格式验证、实测基线与比较 |
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

1. **单一分析事实来源**：各宿主通过共享的 Application 管线执行统计；内存调用可直接使用 Core。
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

GitHub Actions 会在推送到 `main`、相关模块的 Pull Request 或手动触发时生成 `ObjectAnalyzer-Desktop-win-x64` 构建产物。

## 开源协议

请参阅仓库中的许可证文件。
