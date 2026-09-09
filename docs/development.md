# 开发指南

## 项目结构

```text
src/
├── ObjectAnalyzer.Core
├── ObjectAnalyzer
├── ObjectAnalyzer.Api
└── ObjectAnalyzer.Web

tests/
├── ObjectAnalyzer.Tests
├── ObjectAnalyzer.Api.Tests
└── ObjectAnalyzer.Web.Tests
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
