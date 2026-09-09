# 告警与 Webhook

平台可以根据一次分析结果判断是否需要通知外部系统。

## 告警条件

当前支持：

- 综合质量分低于阈值
- Completeness 低于阈值
- 相比上一次成功分析质量下降达到阈值
- 出现关键问题
- 出现 Breaking Schema Change
- Distribution Drift 超过阈值

告警规则可以独立启用或停用，并可配置 Webhook 地址。

## Webhook 投递

Webhook 不在分析线程中直接承担可靠投递，而是进入持久化 Outbox，再由后台 Worker 投递。

```text
Analysis
  ↓
Alert Evaluation
  ↓
Webhook Outbox
  ↓
Delivery Worker
  ↓
HTTP POST
  ├─ Success
  └─ Retry → Permanent Failure
```

失败投递使用递增退避，并限制最大尝试次数。

## 使用建议

Webhook 地址应使用 HTTPS，并避免把密钥、Token 或其他敏感信息写入普通日志和业务数据。

外部系统应按 Job ID / Event ID 做幂等处理，因为可靠投递通常意味着接收方需要能够处理重复请求。
