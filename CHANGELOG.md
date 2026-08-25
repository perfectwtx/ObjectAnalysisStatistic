# Changelog

本项目使用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [Unreleased]

### Added
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
