# Roadmap

> ObjectAnalyzer 已从单一“对象字段分析工具”演进为“对象分析 + 数据质量平台”。
> 当前主线以 `main` 为准，本文只描述已经落地的能力与下一阶段方向。

## 当前状态

### P0 · 核心分析与产品化 ✅

- JSON / JSONL / CSV / Excel / Excel-JSON / XML / YAML 分析
- gzip / bzip2 / zip 压缩输入
- PostgreSQL / MySQL / SQLite / SQL Server 直连
- 字段覆盖率、类型、值分布、数值与时间统计、重复对象分析
- CLI / Mini API / Blazor Web 三种宿主
- JSON / HTML / Markdown / CSV / Excel / PDF 报告
- Schema 代码生成与 Schema 快照

### P1 · 数据质量与门禁 ✅

- 必填、枚举、范围、Pattern、唯一性、Null Rate 等质量规则
- 数据质量报告与结构化违规信息
- 质量门禁、PII 合规门禁、异常值门禁、Schema 演进门禁
- 退出码 3 / 4 / 5 / 6
- PII 自动识别、出站打码与审计日志

### P2 · 工程化与体验 ✅

- 统一分析管线 `AnalysisPipeline`
- `AnalysisRunContext` 运行上下文
- CLI / API / Web 共用 Core 分析能力
- 导出器注册表与质量门禁注册表
- 规则与运行参数统一转换
- Web i18n、预览、导出、冒烟测试
- 大文件进度、断点续跑、内存预算

### P3 · 深度分析 ✅

- 日期 / 时间深度分析
- IQR / Z-Score / 3σ 异常值检测
- Schema Snapshot / Diff
- JSONPath root-path / record-path 投影
- Pearson / Spearman 字段相关性
- 字符串模式归纳
- Unicode 数据卫生检查
- 模糊重复识别
- 时间序列分析
- 采样置信区间
- 分布漂移分析

### P4 · 安全与合规增强 ✅

- AES / JWT / Base64 / SM4 / SM2 字段转换
- PII Policy 与统一出站脱敏
- 审计事件链
- 操作者标识、输入 SHA-256、运行参数审计
- Security Policy

### P5 · 数据质量平台化 ✅

ObjectAnalyzer 当前已经具备独立的数据质量平台骨架：

```text
数据源
  ↓
Analysis Job
  ↓
Analysis Pipeline
  ↓
Analysis Result
  ↓
Quality Metrics / Issues
  ↓
History Store
  ↓
Quality Dashboard
  ├── Quality Trend
  ├── Field Detail
  ├── Regression
  ├── Schema Impact
  ├── Relationships
  └── Recommendations
  ↓
Alert Rules
  ↓
Webhook Outbox
  ↓
Webhook Delivery Worker
```

已完成能力：

- **Analysis Job**：统一记录分析任务状态、耗时、对象数、字段数、质量分、Issue 数及结果
- **Quality Score**：完整性、有效性、唯一性、一致性、异常控制五维评分，并计算 Overall Score
- **Quality Issue Center**：统一展示字段级质量问题及重复对象组等问题
- **SQLite History Store**：保存分析任务、质量快照、字段质量快照、质量问题
- **Quality Dashboard**：质量总览、历史趋势、Top Issues
- **Field Detail**：字段覆盖率、Null Rate、唯一性、异常率及详细分析信息
- **Quality Trend**：基于标准化质量快照展示历史变化
- **Quality Regression**：比较前后两次分析，识别质量分、字段指标和违规项变化
- **Schema Impact**：识别字段新增、删除及破坏性 Schema 变化
- **Relationships**：Pearson / Spearman 字段关联分析
- **Recommendations**：根据质量问题生成治理建议
- **Alert Rules**：质量分、完整性、严重问题、Schema 变化、分布漂移、质量回退规则
- **Webhook Outbox**：可靠投递、失败重试、指数退避和最终失败记录
- **AnalysisExecutionCoordinator**：统一管理任务 Started / Succeeded / Failed / Cancelled 生命周期

平台化设计原则：

1. 标准化指标优先使用结构化历史表，不依赖 `ResultJson` 做趋势与告警计算。
2. 复杂分析结果（如 SchemaDiff、CorrelationMatrix）允许保留在 `ResultJson`，避免为每一种分析结果过度建表。
3. Web 层负责展示，Core / Platform 层负责计算、任务编排和持久化。
4. 新质量规则、新告警条件和新输出能力应优先通过扩展点接入，而不是继续堆积在页面代码中。

## 下一阶段

### P6 · 数据质量运营增强

- [ ] 数据源管理：文件、数据库、API 等统一数据源配置
- [ ] 分析任务配置持久化
- [ ] 定时 / 手动任务调度
- [ ] 数据集与质量规则绑定
- [ ] Quality Profile 页面
- [ ] 质量问题确认、关闭、忽略、责任人和备注
- [ ] Issue 生命周期与治理闭环

### P7 · 质量趋势与告警增强

- [ ] 按数据源 / 数据集 / 字段查看长期质量趋势
- [ ] 告警历史与告警状态
- [ ] Webhook 投递历史页面
- [ ] 告警静默 / 去重 / 聚合
- [ ] 质量回退基线优化
- [ ] 更精确的分布漂移阈值模型

### P8 · 数据治理自动化

- [ ] 自动生成规则建议
- [ ] 字段级修复建议
- [ ] 可选的数据修复任务
- [ ] 修复前后质量对比
- [ ] 规则版本化
- [ ] 质量规则审批流程

### P9 · 数据血缘与影响分析

- [ ] 数据源 → 数据集 → 字段 → 分析任务关系
- [ ] Schema 变化影响范围
- [ ] 字段上下游依赖
- [ ] 数据血缘可视化
- [ ] 质量问题影响面分析

### P10 · 生产化

- [ ] PostgreSQL 历史存储实现
- [ ] 多实例 Web 部署
- [ ] 后台任务队列
- [ ] 权限与角色
- [ ] 多租户隔离
- [ ] 监控与指标
- [ ] Docker / 部署模板

## 非目标

当前阶段不以继续堆叠 CLI 参数为主要目标。新增能力应优先考虑是否能够同时服务 CLI、API、Web 和平台化任务执行。

## 开发原则

- Core 保持与 UI 解耦
- 分析逻辑只存在一个事实来源
- 质量指标优先结构化存储
- 历史数据与当前分析结果分离
- Web 页面不承担核心业务计算
- 失败任务必须可追踪
- Webhook 使用 Outbox 保证可靠投递
- 所有新功能必须有对应自动化测试
