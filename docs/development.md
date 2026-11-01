# 开发指南

## 项目结构

```text
src/
├── ObjectAnalyzer.Core            # 无第三方依赖的计算内核与契约
├── ObjectAnalyzer.Application     # 执行管线、事件与输入端口
├── ObjectAnalyzer.Parsing         # 文件、数据库、压缩与转换适配器
├── ObjectAnalyzer.Exporting       # 报告、Schema 导出与规则导入
├── ObjectAnalyzer.Platform        # 作业、质量基线、责任、修复与告警
├── ObjectAnalyzer.Infrastructure  # SQLite、审计与密钥实现
├── ObjectAnalyzer.Hosting         # 完整命令及模块组合
├── ObjectAnalyzer                 # CLI
├── ObjectAnalyzer.Api             # HTTP API
├── ObjectAnalyzer.Web             # Blazor Web
├── ObjectAnalyzer.Desktop         # WPF Desktop
└── ObjectAnalyzer.Desktop.Core    # 与 UI 无关的 Desktop 模型与服务

tests/
├── ObjectAnalyzer.Tests
├── ObjectAnalyzer.Api.Tests
└── ObjectAnalyzer.Web.Tests

benchmarks/                        # BenchmarkDotNet 工具与实测基线
testsupport/                       # 契约测试与基准共享的固定负载
samples/                           # 示例数据、规则与独立内核应用
```

## 开发原则

### Core 优先

分析逻辑、领域模型和可复用计算能力放在 Core，不把 CLI 或 Web 行为倒灌进核心分析代码。

### 平台能力解耦

任务生命周期、历史、质量快照、问题和告警属于平台层，通过接口与存储实现隔离。

### 可测试

新增分析规则或质量能力时，同时增加核心测试；涉及 HTTP 或页面行为时增加对应宿主测试。

## 本地验证

```bash
dotnet restore ObjectAnalyzer.sln
dotnet build ObjectAnalyzer.sln -c Release
dotnet test ObjectAnalyzer.sln -c Release
```

## 修改文档

文档描述当前系统，而不是历史开发过程。架构发生变化时，优先更新架构、能力和使用文档，再更新 README 的入口索引。
