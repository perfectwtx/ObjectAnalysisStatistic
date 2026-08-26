# Changelog

本项目使用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [Unreleased]

### Added
- **国密算法支持（SM4 / SM2 字段转换，国内合规场景刚需）**：在 `FieldTransform.type` 中新增 `sm4` / `sm2` 两个类型，复用现有 `key` / `iv` / `mode` / `padding` / `format` 字段并扩展 SM2 专用 `privateKey` / `publicKey` / `signature` 三个配置：
  - **SM4**（GM/T 0002-2014）：对称加解密，128-bit 块/128-bit 密钥，支持 CBC（默认）/ ECB 与 PKCS7（默认）/ None 填充；`format` 支持 base64 / hex；CBC 必须提供 `iv`。底层使用 BouncyCastle `SM4Engine` + `CbcBlockCipher` + `Pkcs7Padding`。
  - **SM2**（GM/T 0003-2012）：基于 sm2p256v1 曲线，两种工作模式：
    - `mode: decrypt` — 用 `privateKey` 解密 SM2 密文（公钥加密的密文）；
    - `mode: verify` — 用 `publicKey` 校验 `signature` 是否为 `plainText` 的合法 SM2 签名，返回 `valid` / `invalid`。
  - SM2 密钥支持多种编码自动识别：Base64 DER（PKCS#8 / X.509 SubjectPublicKeyInfo）、PEM（PEM 块带 `-----BEGIN ...-----` 头）、hex（32 字节裸私钥整数 / 64 字节裸 X||Y / 65 字节 04||X||Y 未压缩点）。
  - 实现 `FieldTransformProcessor.ValidateTransform` 严格校验 key/IV 长度、SM2 mode 合法性、密钥与签名字段必填关系；通过 `FieldTransformProcessor.ApplySm4/ApplySm2/ApplySm2Decrypt/ApplySm2Verify` 统一入口。
- **测试（国密）**：新增 `GuomiTransformTests` —— 11 个用例：SM4 ECB 官方测试向量（GM/T 0002-2014 附录 A.1）、SM4 CBC 随机密钥 round-trip、SM4 密文格式自动识别、SM4 失败保留原值、SM2 自生成密钥对加解密 round-trip、SM2 签名验签 round-trip、SM2 验签负例、SM2 密钥格式（hex 32/64/65、PEM、Base64 DER）兼容、SM2 错误密钥长度/格式报错、SM4/SM2 配置校验报错。
- **数据血缘与审计日志（--audit-log / --operator）**：分析运行自动记录合规所需的"谁在何时分析了什么"事件链，以 JSON Lines (NDJSON) 格式追加到指定文件：
  - 四类事件：`analysis_start` / `analysis_finish` / `analysis_cancel` / `analysis_error`；
  - 每条事件含 `runId`（串联同一运行）、`timestamp`（UTC ISO 8601）、`operator`、`toolVersion`、`inputSource`（db://provider?query / 文件路径）、`inputHash`（SHA-256 hex）、`inputBytes`、`options`（关键选项快照）、`result`（仅 finish，含 totalObjects/fields/质量违规数/PII 数/退出码等）、`exitCode`、`message`。
  - **不写入任何敏感数据**：审计日志绝不记录字段值（ValueCounts/Sample/PII 明文），也绝不记录 AES/SM4/SM2 的 `key` / `iv` / `privateKey` / `publicKey` / `signature` 等敏感配置；transform 字段仅记录 `field` + `type` 用于溯源。
  - 写入采用同步 AutoFlush + FileShare.Read，确保审计数据在进程崩溃/被强杀时不丢失，并允许外部日志聚合器并发读取。
  - `AnalysisRunner` 集成：start 事件在所有前置校验通过后写入；finish / cancel / error 在所有返回点（含 0/3/4/5/6 业务门禁失败与未捕获异常）兜底记录；审计路径不可写时仅警告，不阻塞分析主流程。
  - 配合现有 `--operator` 标识执行者（CI runner / 用户邮箱 / 调度器 ID）实现完整溯源。
- **测试（审计日志）**：新增 `AuditLoggerTests` —— 13 个用例：NDJSON 写盘、文件追加模式、空字段默认省略、选项快照不含敏感材料（key/iv/PEM/signature 等）、SHA-256 哈希稳定且 hex、缺失文件返回 null、结果摘要不存值；`AnalysisRunner` 集成 6 个用例：成功 start+finish、runId 串联、退出码 0/3、字段值不泄漏（PII 测试数据）、未启用审计照常运行、关键选项完整记录。
- **安全策略文档 SECURITY.md（中文）**：声明支持版本、数据分类与处理原则、密钥管理建议、审计日志最佳实践、国密算法支持范围、漏洞披露渠道（GitHub Security Advisories / 邮件）与 SLA（首次确认 3 工作日 / 严重问题 30 天修复）、致谢机制、安全特性清单。
- **测试**：JSONPath 单元测试（根路径数组通配、对象匹配、过滤器、嵌套路径、空集、语法错误 4 类 7 个用例）+ JSONPath 集成测试（root-path+record-path 组合、JSONL record-path 提取、CSV/Excel record-path 兼容、空集告警、组合提取后展平回归 7 个用例）。
- **P3 高级分析能力**
  - **日期/时间深度剖析**：`InternalFieldMetrics` 新增 `DateTimeStats` 通道（`Z` / `+08:00` / 无时区三种形态统一按 UTC 入桶），`FieldStatistic` 输出 `DateTimeCount` / `MinDateTime` / `MaxDateTime` + 时间桶直方图（按跨度自适应切档：秒/分/时/天/周/月/年）；流式热路径 O(1) 内存；控制台 / JSON / HTML / Markdown 报告新增「时间序列」小节。
  - **异常值检测（IQR 1.5×）**：复用现有 t-digest 分位数草图计算 P25 / P75 → IQR，基于对数直方图近似归类离群点；`FieldStatistic` 新增 `OutlierCount` / `OutlierRatio` / `OutlierBounds`（流式样本数 < 4 时不计算，避免小样本抖动）；`QualityChecks` 新增 `OutlierRatio` 类型，规则文件支持 `fields.<name>.maxOutlierRatio` 字段级阈值；新增 `--fail-on-outlier-ratio <RATIO>` 全局 CI 门禁（退出码 5）。
  - **Schema 快照 + Diff**：`--export-schema-snapshot <FILE>` 导出 canonical schema JSON（字段名/主导类型/语义/coverage/nullable/范围/枚举/异常值，独立于 `--export-schema` 代码生成器）；`--diff-schema <BASELINE>` 与历史快照对比，识别新增/删除/类型变化/coverage 变化/语义变化等；`--schema-diff-report <FILE>` 导出结构化 diff JSON；`--allow-breaking-schema-changes false` 触发 CI 演进门禁（退出码 6，破坏性变更定义：字段删除、主导类型变化、nullable 收紧、coverage 下降 ≥ 0.1）；控制台 / JSON / HTML / Markdown 报告新增「Schema 演进」小节；可作为下游「数据漂移对比」的字段级基础。
  - **测试**：日期/时间剖析（min/max/直方图计数一致性 + UTC 归一化）、异常值检测（明显离群点被标记 + 区间文本非空 + 小样本保护）、Schema 快照构建与往返、diff 三种场景（新增/删除/破坏性变更门禁），共 12 个新用例。
  - **测试基础设施**：新增 `AnalysisRunnerSequentialGroup` xUnit CollectionDefinition 强制 AnalysisRunner 测试串行执行，规避 Spectre.Console `AnsiConsole.Status()` 在多进程级并行时争用全局句柄的并发异常。
- **压缩文件直读（gzip / bzip2 / zip）**：`.gz` / `.bz2` / `.zip` 压缩包可直接作为输入，无需预先解压——按扩展名识别最外层压缩并流式解包，内层格式由剥离压缩后缀的扩展名决定；扩展名不可判时按头部魔数嗅探兜底（未知扩展名的 Office Open XML 文件按 Excel 解析而非当作 zip 数据容器）；zip 自动选择数据条目（优先已知数据格式、多候选取字典序首个并在控制台提示），且支持 zip 内嵌套 gzip/bzip2 条目（如 `logs.csv.gz`）。行级流式格式（JSONL/CSV/XML/YAML）在解压流上边读边解析，超大压缩文件内存有界；仅内层为需随机访问的 `.xlsx` 时才缓冲到内存。采样与强类型 `--type` 路径照常适用。解析器入口统一改为流式契约（`IFileParser.Parse(Stream)`），由惰性枚举包装负责打开/释放解压源（句柄零泄漏）。
- **测试**：gzip JSONL/JSON/CSV/xlsx、bzip2 JSON、zip 单/多条目确定性选择、仅目录 zip 报错、zip 嵌套 gzip 条目、无扩展名魔数识别、改名 xlsx 内容级识别为 excel、非压缩回归、句柄释放、AnalysisRunner 端到端退出码 0，共 16 个新用例。
- **质量检查 v2（Great Expectations 风格）**：规则文件字段级新增 5 个检查维度——`pattern`（正则模式，截断时近似计数并标记）、`minValue`/`maxValue`（数值范围越界，逐行精确计数、不受唯一值截断影响）、`required`（null 或整行缺失）、`unique`（唯一性：未截断精确统计，截断时 HyperLogLog 近似并标记）、`nullRateMax`（null 率上限，分子含显式 null 与缺失）；顶层新增 `expectations` 数据集期望——`minRowCount`（最少对象数）与 `maxDuplicateRate`（重复对象占比上限）。所有违规统一汇总至 `AnalysisResult.QualityViolations` 并接入 `--fail-on-violations` 门禁（退出码 3）、`--violations` 导出（升级为 `{ summary, fieldViolations, datasetViolations }` 对象结构，枚举条目保留旧版逐值 `values` 键向后兼容）与数据质量报告（`Checks` 字段级条目 + 顶层 `DatasetViolations`）；无效正则在加载规则文件时立即报错退出；PII 打码同步作用于违规样本。
- **测试**：范围越界截断场景精确计数、required 缺失+null 合计、nullRateMax 边界通过/超出、pattern 命中与截断近似标志、unique 精确重复样本与 HLL 近似区间、minRowCount/maxDuplicateRate 通过与失败、`--fail-on-violations` 退出码 3 / 干净数据退出 0 / 无效正则退出 1 集成、`--violations` v2 结构回读、数据质量报告新维度与旧枚举语义兼容、PII 掩码传播到违规样本、纯枚举旧规则向后兼容聚合、JSON 往返，共 19 个新用例。
- **数值/语义统计深度补齐**：数值列新增样本标准差（Welford 在线算法，O(1) 内存）、近似分位数 P50/P90/P95/P99（简化 t-digest 质心草图，容量有界且支持多线程合并）、2 的幂对数直方图分桶（负数/零/正数桶按值域升序，单遍流式无需预知范围）；字符串列新增平均长度；全部标量字段新增 distinct 值计数——唯一值未超过跟踪阈值时为精确计数，超出时自动退化为 HyperLogLog 近似估计（p=14，稀疏→稠密渐进存储 + LinearCounting 校正消除 n≈m 过渡区正偏差，标准误差 ≈0.8%），以 `DistinctApproximate` 标志区分。JSON / 控制台 / HTML / Markdown / CSV 报告同步展示；CSV 表头新增 `StdDevNumber`/`MedianNumber`/`P90Number`/`P95Number`/`P99Number`/`Histogram`/`AvgLength`/`DistinctApproximate` 列，`UniqueValues` 列升级为真实 distinct 计数。
- **测试**：Welford 均值/样本标准差闭式解对比、t-digest 分位数均匀/重尾分布容差与"合并 ≈ 单遍"等价性、直方图桶计数总和与区间顺序、HyperLogLog 多样本平均精度（<2%）与稀疏合并一致性、并行分析深度指标稳定性、JSON 序列化往返、脱敏克隆保留新字段、CSV 新列冒烟，共 17 个新用例。
- **PII 误报逃生口**：规则文件字段级 `redact: false` 白名单——字段名启发式误伤合法值时（如 name 列存产品名），声明后该字段不打码、不进合规报告；优先级高于 `pii` 自定义规则。新增 `--pii-off` 全局关闭 PII 识别与打码，报告输出原始值（与 `--fail-on-pii-level` 互斥、同用 `--pii-report` 时忽略后者）。
- **PII 国际类型**：新增 SSN（`ddd-dd-dddd`）、IBAN（紧凑/带空格分组格式 + ISO 7064 MOD 97-10 校验位验证）、护照号（1-2 位字母 + 6-9 位数字，需 passport/护照 字段名提示）三类语义识别与确定性打码（`***-**-6789`、`DE89***************000`、`E*****678`），全部归入高危敏感类别。
- **PII 校验位验证降低误报**：18 位身份证号需通过 GB 11643 MOD 11-2 校验位（15 位一代证仅形态匹配）；16-19 位银行卡号需通过 Luhn 校验。随机数字串不再被误判为身份证/银行卡；形态命中但校验失败的值不再进入 PII 报告与打码流程。
- **CI 合规门禁 `--fail-on-pii-level <A|B|C>`**：分析完成后计算合规等级，达到或劣于阈值时以退出码 `4` 结束（`C`=仅 C 级失败；`B`=发现任何敏感字段即失败），与 `--fail-on-violations`（退出码 `3`）对称；非法级别值与互斥参数组合在启动时报错。
- **敏感数据识别 (PII) 与报告打码**：自动识别手机号（`1[3-9]\d{9}`）、身份证号（15/18 位）、银行卡号（16-19 位）、姓名（字段名提示 + 纯字母/CJK 值形态）与邮箱字段，并写入 `FieldStatistic.SemanticType`（新增 `Phone` / `IdCard` / `BankCard` / `Name` 语义类别）。所有报告输出（控制台、JSON、HTML、Markdown、CSV、数据质量/违规清单导出，含 Web 下载链接）在导出前经 `PiiRedactor.Redact` 统一打码——值频次键、枚举违规值与重复对象 JSON 中的敏感值按语义打码（`138****1234`、`110101********7528`、`6222********7890`、`张*`、`al***@example.com`），打码确定性且幂等；无敏感字段时零开销原样返回。
- **数据安全合规等级报告**：`--pii-report <FILE>` 输出结构化 JSON（`PiiReport` / `PiiExporter`）——合规等级 A/B/C（A=未发现敏感字段；B=存在敏感字段且已全部自动打码；C=样本无法完全打码需人工复核）+ 敏感字段清单（字段名/类别/高危标记/覆盖率/打码样本）+ 各类别统计；控制台在每次分析后同步输出一行"数据安全合规"摘要。
- **测试**：PII 语义识别（手机号/身份证/银行卡/姓名/邮箱）、打码规则（10 组掩码样例）、`PiiRedactor` 打码不改动原始结果、重复对象 JSON 脱敏、JSON 报告无原始 PII 泄漏、`PiiReport` 等级 A/B 构建、`PiiExporter` 合规快照回读，共 21 个新用例。
- **测试（PII 补强）**：校验位验证器（GB 11643 / Luhn / IBAN MOD 97 各合法与非法样例）、非法校验位值不再误判、15 位一代证兼容、SSN/IBAN/护照识别与打码掩码、护照字段名提示必要性、`redact: false` 白名单（含覆盖 `pii` 自定义规则、报告排除）、`--pii-off` 导出原始值、`--fail-on-pii-level` 退出码 0/4 集成用例，共 33 个新用例。
- **Web：浅色/深色主题切换**：顶部新增主题按钮（🌙/☀️），基于 Bootstrap 5.3 `data-bs-theme` 全站切换；选择持久化到 `localStorage`，`App.razor` 内联脚本在首次绘制前套用已保存主题（无闪烁）；`app.css` 为自定义组件（配置面板/上传区/图表盒/字段表/覆盖率条/预览等）补齐深色覆盖（复用 Bootstrap CSS 变量）；新增 i18n 键 `ThemeDarkTitle`/`ThemeLightTitle`。
- **Web：覆盖率/类型分布图表**（Chart.js，`Home.razor` + `interop.js` 渲染函数）。
- **Web：数据预览**（前 8 行 x 前 8 列，`AnalysisState.Preview`）。
- **Web：报告下载**（JSON / HTML / MD / CSV，复用 `ReportExporter.Save` + `downloadTextFile`）。
- **Web：Schema 导出下载**（C# / TypeScript / JSON Schema / SQL，SQL 支持 PostgreSQL / MySQL / SQLite / SQL Server 方言下拉，复用 `SchemaExporter.Generate*`）。
- **CLI：dotnet tool 打包**（`PackAsTool` + `ToolCommandName=objectanalyzer`，`dotnet pack` 产出 `artifacts/packages`）。
- **Docker：多阶段 Dockerfile**（SDK 10 构建，runtime 10 运行，工作目录 `/data`）。
- **CI：增加 dotnet tool pack 步骤**。
- **数据质量报告 (P1)**：`--quality <FILE>` 导出结构化 JSON；`--fail-on-violations` 在存在枚举违规时以退出码 `3` 结束，供 CI 质量门禁。
- **报告质量小节**：Markdown / HTML 报告增加“数据质量”摘要（✅/❌ + 违规率）。
- **i18n**：resx 本地化（默认中文 + `AppStrings.en.resx` 英文），顶部语言切换按钮。
- **超大文件采样分析**：`--sample-reservoir <N>`（蓄水池随机抽样，Algorithm R）与 `--sample-step <K>`（等距系统抽样），在 50GB+ 数据导出上秒级获取 ~99% 准确度的 Schema 概览；`jsonl/csv/excel/excel-json` 在行/记录级跳过未选中记录（不解析），内存占用有界（≤ N / K）。
- **SQL Schema 导出**：`--export-schema sql`（默认 PostgreSQL）`/postgres`/`mysql`/`sqlite`/`sqlserver` 生成 CREATE TABLE DDL；基础字段自动映射类型（Number → BIGINT/DOUBLE/FLOAT，DateTime → TIMESTAMP/DATETIME2，Boolean → BOOLEAN/TINYINT(1)/BIT 等），覆盖率 100% 且未出现 null 的字段生成 `NOT NULL`，嵌套对象/数组折叠为方言 JSON 列（JSONB/JSON/NVARCHAR(MAX)/TEXT），枚举规则值生成 `CHECK (col IN (...))` 约束，表名取自输出文件名。
- **数据库直连 (Live DB Ingestion)**：新增 `--connection <连接字符串>` + `--query <SQL>` 直连 PostgreSQL / MySQL / SQLite / SQL Server 分析（`--provider` 显式指定或按连接字符串关键字自动推断）。基于 `DbDataReader` 惰性逐行流式读取，无需先导出为 CSV/JSON 即可实时分析结构与质量；数据库原生值归一化为与分析管道一致的标量（数值→long/double、布尔→bool、DateTime→ISO-8601、Guid→字符串、二进制→Base64、NULL→null），并支持 `--sample-reservoir` / `--sample-step` 采样与 `-s` Schema/代码导出（表名缺省 `database`）。
- **测试**：数据质量快照回读、报告导出器快照（JSON/MD/HTML/CSV）、Web 冒烟（`WebApplicationFactory`）、i18n 单元测试、采样（SamplingTests）测试、SQL DDL（三种方言 + CHECK/可空性）测试、数据库直连（SQLite 流式读取 + 类型归一化 + 连接字符串推断）测试。

## [2026-08-10]

### Fixed
- JSON 解析剥离 UTF-8 BOM，修复流式解析回归。
- `CancelActiveAnalysis` 优化，避免文件替换/清除时误报“分析已取消”。

## [2026-08-09]

### Fixed
- Web 上传/清除竞态、报告增量写入、`TypedFileParser` 重复读取、XML 重复元素聚合、CLI Ctrl+C 取消。
- `EnumViolations` 只读封装、数组热路径优化、O(1) 分块切片、YAML 扁平化、XML 属性支持、`ConsoleReporter` 编码。
- `SchemaExporter` 反扁平化还原嵌套 DTO 与强类型数组；`EndsWith` 编译错误；计数器升级为 `long`。

### Changed
- **PII 识别更严格（潜在行为变化）**：18 位身份证号与 16-19 位银行卡号现在需通过校验位验证才被判定为敏感语义；此前仅凭数字形态即判定的随机串不再触发打码。显式规则声明（`semanticType` 规则 / `pii` 自定义规则）不受影响。
- `Hash128` 替代 MD5，流式 JSON 解析，Web 懒管道 + 取消，XML 防护。

## [2026-08-08]

### Added
- 流式解析、数值与语义类型指标（Min/Max/Avg/Sum、Email/UUID/DateTime 推断）、Schema 导出器、UX 进度。
- 重复 JSON 存储上限、`TotalDuplicateGroups` 精确统计、`[Other values]` 不占名额、`TypeLoader` 并发锁、清理死代码。
- 修复哈希碰撞/不可序列化崩溃/CSV 注入等审查问题；临时文件电路断开清理。

## [2026-08-07]

### Changed
- Web：开始分析按钮始终可见，未选文件时点击提示；显式开始分析按钮 + 缩短选项输入框宽度；移除侧边栏、粘性应用栏、可折叠配置面板、网格化选项。
- CLI：`--parallel` 校验拒绝 `-1` 以外的负值；修正 `--output` 帮助文本；Web 清除/下载增强；CSV 覆盖率小数；CI 超时。
