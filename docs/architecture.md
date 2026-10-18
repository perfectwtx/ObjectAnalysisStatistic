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

各职责已拆为独立程序集。Core、Application 和 Platform 均无第三方包；Core 不引用其他项目。

| 项目 | 职责 | 直接项目依赖 |
|---|---|---|
| Core | 值、路径、统计状态、规则、Schema 推断、结果投影、质量指标 | 无 |
| Application | 执行计划、逐次运行管线、事件、输入/转换端口 | Core |
| Parsing | 文件、压缩、数据库、JSONPath 和加密转换 | Core、Application |
| Exporting | 报告、Schema 后端、快照 IO、C# 规则导入 | Core |
| Platform | 作业、历史端口、基线、责任、修复、告警业务 | Core |
| Infrastructure | SQLite 存储、审计文件和密钥实现 | Core、Platform |
| Hosting | 完整命令运行、预检、输出、模块组合与 DI 注册 | 上述六个项目 |

计算内核和应用管线不选择具体存储、解析器或报告实现。内存统计只需引用 Core；需要事件和采样的自定义执行可引用 Application 并实现输入端口；使用所有内置能力可通过 Hosting 的 AnalysisRuntime.CreateExecutor() 构造执行器。

平台层定义 AnalysisJob、QualitySnapshot、FieldQualitySnapshot、QualityIssue 等业务模型，AnalysisExecutionCoordinator 处理作业开始、成功、失败与取消；数据库实现属于 Infrastructure。质量指标计算属于 Core，作业结果的脱敏交付属于 Platform，避免内核反向引用平台实体。

具体引用方式、依赖归属与破坏性变更见 [模块拆分与迁移](core-module-boundaries.md)。

### Hosts

- CLI：面向本地和自动化命令行场景。
- API：面向系统集成和前端调用。
- Web：面向交互式分析和质量管理。
- Desktop：面向 Windows 本地交互式分析和平台业务。

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

新增统计优先扩展 Core 的分析器和规则，新增执行步骤放入 Application；输入适配器实现 Application/Core 的端口，报告实现放入 Exporting，平台业务通过存储端口调用 Infrastructure 的实现。宿主和 Hosting 负责组合，内层项目不能引用外层项目。
