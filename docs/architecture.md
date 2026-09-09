# 总体架构

## 定位

ObjectAnalyzer 将一次数据分析抽象为可追踪的 Analysis Job，并在分析结果之上建立数据质量、问题、历史和告警能力。

```text
数据源
  ↓
解析 / 转换 / 过滤 / 采样
  ↓
AnalysisPipeline
  ↓
AnalysisResult
  ├─ 字段统计
  ├─ Schema
  ├─ 数据质量
  ├─ 异常 / 漂移
  └─ 深度分析
       ↓
   AnalysisJob
       ↓
质量快照 / 字段快照 / 问题
       ↓
历史查询 / Dashboard / 告警 / Webhook
```

## 核心分层

### Core

`src/ObjectAnalyzer.Core` 负责解析、分析、质量检查、Schema、深度分析和报告能力。

核心计算不依赖 Web UI 或 CLI 渲染实现。

### Platform

平台层负责一次分析的生命周期和持久化：

- `AnalysisJob`：一次分析任务。
- `AnalysisExecutionCoordinator`：统一处理开始、成功、失败、取消。
- `AnalysisHistoryStore`：保存任务和质量历史。
- `QualitySnapshot`：数据集级质量快照。
- `FieldQualitySnapshot`：字段级质量快照。
- `QualityIssue`：标准化质量问题。
- Alert / Webhook：根据分析结果触发外部通知。

### Hosts

- CLI：面向本地和自动化命令行场景。
- API：面向系统集成和前端调用。
- Web：面向交互式分析和质量管理。

## 一次分析的生命周期

```text
Pending → Running → Succeeded
                  ├→ Failed
                  └→ Cancelled
```

成功任务会生成质量快照和字段快照；配置了告警规则时，还会进入告警评估和 Webhook 投递流程。

## 持久化原则

标准化、需要查询和趋势分析的数据进入独立表；复杂且变化较快的分析结构保留在 `ResultJson`。

当前实现使用 SQLite 作为历史存储，存储抽象通过接口隔离，后续可以替换为 PostgreSQL 等实现。

## 扩展原则

新增分析能力优先扩展 Core 的领域模型和 Pipeline；新增展示或集成能力优先通过 Platform 接口扩展，避免让分析内核直接依赖具体宿主。
