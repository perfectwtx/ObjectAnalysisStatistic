# 安全策略（Security Policy）

本文档说明 ObjectAnalyzer 在产品安全、数据合规与漏洞披露方面的承诺与流程。
工具自身不存储任何业务数据，所有分析在内存中完成；但分析过程中可能读取含敏感信息
（个人身份信息 PII、加密字段等）的输入文件，因此部署时仍需遵循以下安全指引。

---

## 1. 支持版本（Supported Versions）

下表为当前获得安全更新与合规增强支持的版本范围：

| 版本 | 支持状态 |
| ---- | -------- |
| 最新 minor（持续滚动） | ✅ 活跃支持（Active） |
| 上一 minor | ✅ 仅修复严重安全问题 |
| 更早版本 | ❌ 不再提供更新 |

我们强烈建议在生产环境使用最新 minor 版本并通过 `--version` 与审计日志记录的工具
版本（`toolVersion` 字段）双向核对。

---

## 2. 数据分类与处理原则

### 2.1 输入数据分类

| 数据类别 | 示例 | 默认处理 |
| -------- | ---- | -------- |
| 公开数据 | 公共 API 响应 | 原样分析 |
| 内部数据 | 业务日志、聚合统计 | 原样分析（建议评估合规要求） |
| 敏感数据（PII） | 手机号、邮箱、身份证、银行卡 | 自动识别 + 报告打码（`--pii-off` 可关闭） |
| 加密字段 | AES / SM4 / SM2 密文、JWT | 通过规则文件 transform 解密后再分析（密钥不出内存） |

### 2.2 脱敏与打码（默认行为）

工具默认启用 PII 识别与打码：
- 报告（JSON / HTML / MD / CSV）中敏感值以 `138****8000` / `ali****@example.com` 形式打码
- 控制台报告同样打码
- 通过 `--pii-off` 可关闭识别与打码（仅在已确认数据不含 PII 时使用）

### 2.3 密钥与敏感配置

- AES / SM4 / SM2 转换所需的密钥、IV、私钥、签名仅保存在规则文件（用户提供的 `*.json`）中
- 审计日志（`--audit-log`）**绝不**记录任何 `key` / `iv` / `privateKey` / `publicKey` / `signature` 字段值
- 报告导出文件同样不包含密钥；请妥善保管规则文件
- 建议对规则文件设置文件系统级访问控制（chmod 600 / ACL）

### 2.4 进程内行为

- 输入文件以**流式**方式读取，避免整体物化到磁盘
- 临时文件（解压、网络下载）若生成在系统临时目录，进程退出后由 OS 回收
- 默认**不**收集任何遥测、崩溃报告或使用统计
- Web 模式（`ObjectAnalyzer.Web`）处理的文件不离开本机：所有分析在前端调用后端 API
  完成，不上传至任何远端服务

---

## 3. 审计日志（Audit Log / Data Lineage）

工具支持通过 `--audit-log <path>` 输出 NDJSON 格式的操作审计记录：
- 每行一条 JSON 对象，对应一次事件（`analysis_start` / `analysis_finish` / `analysis_cancel` / `analysis_error`）
- `runId` 字段串联同一分析运行的多条事件
- `operator` 字段（`--operator`）记录执行者身份（CI runner、用户邮箱、调度器 ID）
- `inputHash` 字段为输入文件的 SHA-256 哈希，用于溯源但不暴露数据
- `inputBytes` 字段为输入字节数
- `options` 字段记录本次分析的关键参数（字段选择、过滤、采样、合规门禁、transform 类型等）
- `result` 字段（仅 finish）记录 `totalObjects` / `totalFields` / 质量违规数 / PII 字段数 / 退出码 等
- **审计日志绝不包含任何字段值（ValueCounts / Sample / PII 明文等）**

### 推荐实践

1. 在生产/合规场景**始终**启用 `--audit-log`，并将审计日志写入只允许追加（append-only）的目录
2. 通过 `--operator` 明确传入执行者标识，避免使用默认的 `anonymous`
3. 配合日志聚合系统（Loki / Splunk / ELK）进行长期归档与查询
4. 定期校验 `inputHash` 与 `options` 的一致性，及时发现"谁在何时分析了什么"的异常

---

## 4. 国密算法支持（Chinese Cryptographic Standards）

为支持国内合规场景（金融、政务、医疗），工具在字段转换（transform）中支持：

| 算法 | 用途 | 来源 |
| ---- | ---- | ---- |
| SM4 | 对称加解密（CBC / ECB，PKCS7 / None 填充） | GM/T 0002-2014 |
| SM2 | 非对称解密（私钥） / 签名验签（公钥） | GM/T 0003-2012 |

实现采用 BouncyCastle .NET（`BouncyCastle.Cryptography` 2.4+）库，遵循：
- SM4 128-bit 块、128-bit 密钥
- SM2 基于 sm2p256v1 曲线（GM/T 0003-2012 附录 A）
- 支持多种密钥编码：Base64 (DER) / PEM / hex 裸字节

SM4 单元测试使用 GM/T 0002-2014 附录 A.1 公开测试向量验证正确性。

---

## 5. 漏洞披露（Vulnerability Disclosure）

我们鼓励负责任的漏洞披露（Coordinated Disclosure）。如果您在工具中发现安全问题：

### 5.1 报告渠道

| 渠道 | 地址 |
| ---- | ---- |
| GitHub Security Advisories（推荐） | https://github.com/your-org/object-analyzer/security/advisories/new |
| 邮件 | security@[your-domain].com（GPG 公钥见本仓库 `docs/security/pgp.asc`） |
| 私密 Issue | 仅在不便使用上述渠道时联系维护者微信/钉钉群 |

### 5.2 报告内容建议

请尽量包含：
- 漏洞复现步骤（含完整命令与样例数据，**请对样例数据做脱敏**）
- 触发版本（`--version` 输出）
- 潜在影响范围（本地提权 / 信息泄露 / 拒绝服务 / 远程代码执行 / 合规违规）
- 已尝试的缓解方案

### 5.3 响应时效承诺（SLA）

| 阶段 | 承诺 |
| ---- | ---- |
| 首次确认（确认收到并分派处理人） | 3 个工作日内 |
| 严重性评估（CVSS 评分 / 影响面） | 7 个工作日内 |
| 修复发布（Critical / High） | 30 个自然日内 |
| 修复发布（Medium / Low） | 下一 minor 版本窗口 |

### 5.4 范围外（Out of Scope）

以下情况不属于安全漏洞，不在披露流程内：
- 工具对自身**未声明**的输入格式（未实现的文件类型 / 协议）崩溃
- 用户主动关闭安全特性（`--pii-off` / 关闭审计日志）后的隐私问题
- 依赖库的已知问题（请同时向 BouncyCastle / Spectre.Console / .NET 上游反馈）
- 拒绝服务攻击：使用工具对超大文件分析（超 GB 级）触发的 OOM

---

## 6. 安全特性清单（Security Features）

| 类别 | 能力 | 启用方式 |
| ---- | ---- | -------- |
| 加密 | SM4 / SM2 字段转换 | 规则文件 `transform.type` |
| 合规 | PII 自动识别 + 打码 + 合规报告 | `--pii-report`（默认开启） |
| 合规 | 合规门禁（CI 失败） | `--fail-on-pii-level A/B/C` |
| 血缘 | 审计日志（NDJSON） | `--audit-log` |
| 血缘 | 操作者标识 | `--operator` |
| 质量 | 数据质量门禁 | `--fail-on-violations` |
| 质量 | 异常值门禁 | `--fail-on-outlier-ratio` |
| 演进 | Schema 破坏性变更门禁 | `--allow-breaking-schema-changes=false` |
| 静态扫描 | CodeQL（GitHub Actions） | `.github/workflows/codeql.yml` |
| 依赖审计 | `dotnet list package --vulnerable` | CI workflow |

---

## 7. 致谢（Acknowledgements）

感谢以下研究者和安全社区对工具的反馈：

（首次披露后由维护者在此添加公开致谢；如需匿名请在报告中说明）

---

如对本政策有疑问或建议，欢迎通过 [Discussions](https://github.com/your-org/object-analyzer/discussions) 反馈。
