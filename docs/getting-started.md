# 快速开始

ObjectAnalyzer 是一个面向结构化与半结构化数据的对象分析与数据质量平台。

## 1. 环境要求

- .NET 10 SDK
- Windows / Linux / macOS

## 2. 从源码运行

在仓库根目录执行：

```bash
dotnet restore ObjectAnalyzer.sln
dotnet build ObjectAnalyzer.sln -c Release
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

导出 HTML 报告：

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html
```

## 3. 启动 Web

```bash
dotnet run --project src/ObjectAnalyzer.Web
```

启动后使用浏览器访问终端输出的地址。

Web 当前包含质量总览、趋势、字段质量、Schema 影响、对象关系、数据血缘、影响分析和问题中心等页面。

## 4. 启动 API

```bash
dotnet run --project src/ObjectAnalyzer.Api
```

API 提供 HTTP 分析能力以及 OpenAPI/Scalar 文档。

关系与影响分析接口包括：

- `GET /api/analysis/jobs/{jobId:guid}/lineage`：查询数据血缘
- `GET /api/analysis/jobs/{jobId:guid}/impact`：查询 Schema 变化影响

## 5. Docker

仓库提供 Dockerfile，用于构建 CLI 运行镜像。当前镜像定位为 CLI 执行环境，不替代 Web 或 API 宿主。

构建镜像：

```bash
docker build -t objectanalyzer:latest .
```

查看 CLI 帮助：

```bash
docker run --rm objectanalyzer:latest --help
```

## 6. 运行测试

```bash
dotnet test ObjectAnalyzer.sln -c Release
```

## 7. 第一次使用建议

建议按照以下顺序了解项目：

1. 先使用 CLI 分析 `samples/test_data.json`。
2. 查看字段统计、质量问题和 Schema 信息。
3. 再启动 Web 查看统一分析结果。
4. 需要自动化集成时使用 API。
5. 需要持续监控时阅读数据质量、历史、告警文档。

## 8. 文档导航

- [总体架构](architecture.md)
- [分析引擎](analysis.md)
- [数据质量](data-quality.md)
- [问题中心](issues.md)
- [历史与趋势](history.md)
- [告警与 Webhook](alerts.md)
- [配置与参数](configuration.md)
- [CLI](cli.md)
- [API](api.md)
- [安全](security.md)
- [开发指南](development.md)
- [路线图](roadmap.md)
