# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / Excel-JSON 列 / XML / YAML 输入（可直接读取 gzip / bzip2 / zip 压缩包），自动统计字段频次、数据类型、值分布、默认值推断、枚举约束校验、多维度质量检查（模式/范围/必填/唯一/null 率 + 数据集期望）及重复对象。

- CLI 命令行工具 (`src/ObjectAnalyzer`) + Blazor 交互式 Web 界面 (`src/ObjectAnalyzer.Web`)，共用核心分析库 (`src/ObjectAnalyzer.Core`)

**输入与解析**

- 多格式支持：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）、XML、YAML；压缩包直读（gzip / bzip2 / zip，含 zip 内嵌套 gzip/bzip2 条目）
- 数据库直连：`--connection` + `--query` 直连 PostgreSQL / MySQL / SQLite / SQL Server，DbDataReader 惰性流式读取，无需先导出为文件即可分析（支持 `--sample-*` 采样与 `-s` Schema 导出）
- 规则驱动值解析：规则文件 `valueParsers` 指定源字段/列并按 `parseType` 解析 JSON / JSON 字符串（Excel 多列 JSON 等场景）
- JSONPath 投影：`--root-path`（文件根提取子节点，如 `$.data[*]`）与 `--record-path`（每条记录提取子对象后再分析，如 `$.payload`），完整支持 RFC 9535 语法（通配符/递归下降/过滤器）；语法错误硬错误快速失败（退出码 1），匹配空集时仅警告并继续；可独立或组合使用
- 流式解析：JSONL 按行惰性读取 + 分块并行聚合，超大文件内存可控

**统计与深度剖析**

- 基础统计：字段频次/覆盖率/主导数据类型、唯一值频次与占比、默认值推断（`null` / `0` / `false`）、重复对象检测（128 位非密码学哈希去重，快于 MD5）、嵌套扁平化（`a.b.c` 路径形式，可选）
- 数值/文本深度剖析：数值列输出均值、样本标准差（Welford 流式算法）、近似分位数 P50/P90/P95/P99（可合并 t-digest 草图）与 2 的幂对数直方图；文本列输出平均长度；所有标量字段提供 distinct 计数（超阈值自动切换 HyperLogLog 近似，内存有界）
- 日期/时间深度剖析：自动识别 DateTime / DateTimeOffset 字符串（`Z` / `+08:00` / 无时区三种形态统一按 UTC 入桶），输出最小/最大时间戳与时间桶直方图（按数据跨度自适应切档：秒/分/时/天/周/月/年）；流式热路径 O(1) 内存
- 异常值检测（IQR 1.5×）：数值列自动统计离群点数量与占比，列出 top-N 离群值所在字段；规则文件 `fields.<name>.maxOutlierRatio` 按字段设置阈值；`--fail-on-outlier-ratio` 提供 CI 门禁（退出码 5）
- 分析深度增强（P4）：字段相关性矩阵（Pearson/Spearman）、字符串模式归纳、Unicode 卫生检查、模糊去重、时间序列深度分析（趋势/周期/间隙）、采样置信区间、分布漂移对比（草图 vs 草图）——详见「分析深度增强（P4）」一节

**质量与 CI 门禁**

- 数据质量报告与门禁：`--quality` 输出 Pass/Fail 摘要 + 违规率 + 逐字段明细；`--violations` 输出结构化违规清单；`--fail-on-violations` 作为 CI 质量门禁（退出码 3）；质量检查 v2 覆盖模式/范围/必填/唯一/null 率 + 数据集期望
- 枚举约束校验：规则文件声明合法枚举值，自动校验并报告违规值
- 从代码 / Schema 生成规则：`rules-from-code`（C# 类/枚举静态解析）与 `rules-from-schema`（JSON Schema）生成规则文件骨架，支撑「模型优先」的数据校验工作流——详见「从代码 / Schema 生成规则文件」一节
- Schema 代码导出与演进：`--export-schema csharp|typescript|json-schema|openapi|openapi-yaml|proto|sql` 生成数据结构定义；`--export-schema-snapshot` 导出 canonical Schema 快照（字段名/类型/coverage/nullable/语义/范围/枚举/异常值），`--diff-schema <BASELINE>` 与历史快照对比，`--allow-breaking-schema-changes false` 触发 CI 演进门禁（退出码 6）
- 字段选择与值转换：`--fields` 仅统计指定字段；规则文件 `transform` 支持 JWT 解码 / AES 解密 / Base64 / URL 解码（失败自动保留原始值并计数），以及 `parse: "json"` 解密后对象展开（`payload.city`、`payload[0]`）
- 字段值过滤：`--where` 支持 `==`、`!=`、`>`、`<`、`>=`、`<=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感；亦可由规则文件 `filter` 提供（CLI 优先级更高）

**安全合规**

- PII 识别与打码：自动识别手机号 / 身份证号 / 银行卡号 / 姓名 / 邮箱 / SSN / IBAN / 护照号（身份证按 GB 11643、银行卡按 Luhn、IBAN 按 MOD 97 校验位验证，降低纯形态误报）；所有报告输出在导出前按语义自动遮蔽（如 `138****1234`、`110101********7528`、`张*`、`al***@example.com`），杜绝原始敏感值外泄；规则文件可声明业务敏感字段（`fields.<name>.pii`）、白名单豁免误报（`fields.<name>.redact: false`）、全局关闭（`--pii-off`）
- 数据安全合规报告：`--pii-report` 输出合规等级（A/B/C）+ 敏感字段清单 + 打码样本 + 各类别统计；`--fail-on-pii-level <A|B|C>` 合规门禁（退出码 4）
- 国密算法字段转换：`transform.type` 支持 `sm4`（GM/T 0002-2014 对称加解密，CBC/ECB + PKCS7/None）与 `sm2`（GM/T 0003-2012 私钥解密 / 公钥验签），适配金融/政务/医疗等国内合规场景；SM2 密钥自动识别 Base64 DER / PEM / hex
- 数据血缘与审计日志：`--audit-log` 输出 NDJSON 事件链（`analysis_start` / `analysis_finish` / `analysis_cancel` / `analysis_error`），含 `runId`、`--operator` 操作者、输入 SHA-256 哈希、关键选项快照与退出码；**绝不**记录字段值与 AES/SM4/SM2 密钥等敏感材料；同步 AutoFlush 保证崩溃时审计数据不丢
- 安全策略：见 [SECURITY.md](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/SECURITY.md) — 支持版本矩阵、数据分类、密钥管理、漏洞披露渠道（GitHub Security Advisories + 邮箱）与响应 SLA

**报告与交付**

- 六种报告格式：结构化 JSON、富文本 HTML、Markdown、CSV、Excel（.xlsx 多 sheet，覆盖字段统计/重复对象/违规清单/全部 P4 七项分析）与 PDF（QuestPDF 结构化排版）；CLI 按 `-o` 扩展名自动路由，Web 界面提供下载按钮
- 超大文件采样分析：`--sample-reservoir`（蓄水池随机）与 `--sample-step`（等距系统），50GB+ 级别数据数秒内获取 ~99% 准确度的 Schema 概览，内存有界
- 分发方式：`dotnet tool` 打包（`objectanalyzer`）、Docker 镜像、Blazor Web 界面（含图表/预览/下载/i18n/主题切换）

## 目录

[快速开始](#快速开始) · [项目结构](#项目结构) · [构建](#构建) · [CLI 使用方式](#cli-使用方式) · [安装](#安装) · [规则文件格式](#规则文件格式) · [KMS 集成](#kms-集成规则文件敏感密钥托管) · [质量检查](#质量检查规则文件-v2) · [从代码 / Schema 生成规则文件](#从代码--schema-生成规则文件) · [自定义 PII 规则](#自定义-pii-规则业务敏感字段--打码格式) · [PII 误报逃生口](#pii-误报逃生口与全局开关) · [CI 合规门禁](#ci-合规门禁) · [数据血缘与审计日志](#数据血缘与审计日志) · [国密算法](#国密算法sm4--sm2) · [高级分析能力（P3）](#高级分析能力p3) · [分析深度增强（P4）](#分析深度增强p4) · [Web 界面](#web-界面) · [Excel / PDF 报告导出](#excel--pdf-报告导出) · [JSONPath 投影](#jsonpath-投影仅分析指定节点下的值) · [超大文件采样](#超大文件采样分析) · [压缩文件直读](#压缩文件直读) · [性能基准](#性能基准) · [技术栈](#技术栈) · [贡献指南](#贡献指南) · [许可证](#许可证)

## 快速开始

仓库代码**无需预先安装**即可从源码直接运行；也可安装为全局 `dotnet tool`，使用 `objectanalyzer` 命令。下面以内置示例数据演示最小可运行流程。

**方式一：从源码运行（推荐首次体验）**

```bash
# 在仓库根目录执行，分析内置示例（首次会自动还原依赖并构建）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json

# 导出 HTML 可视化报告
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html
```

**方式二：安装为 dotnet tool（全局可用）**

```bash
dotnet pack src/ObjectAnalyzer/ObjectAnalyzer.csproj -c Release
dotnet tool install --global --add-source artifacts/packages ObjectAnalyzer.Tool
objectanalyzer -i samples/test_data.json -o report.html
```

更多安装方式（Docker、Web 界面）见下文「安装」一节；完整命令行选项与各场景示例见「CLI 使用方式」一节。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本

## 项目结构

解决方案分为三层：**核心分析库**（CLI 与 Web 共用）+ **两个薄宿主**（命令行、Blazor 界面）。

```
ObjectAnalyzer.sln
├── src/
│   ├── ObjectAnalyzer.Core/        # 核心分析库（命名空间 ObjectAnalyzer.*）
│   │   ├── Analysis/               # 引擎与运行时：分析引擎、AnalysisRunner、选项/结果、
│   │   │   │                       #   增量检查点、采样、内存预算
│   │   │   ├── Fields/             #   字段统计与草图（FieldStatistic、NumericSketch、HyperLogLog）
│   │   │   ├── Insights/           #   深度分析（时间序列、字符串模式、相关性、Unicode、模糊去重）
│   │   │   ├── Drift/              #   分布漂移与快照对比
│   │   │   ├── Quality/            #   数据质量检查（枚举/范围/必填/唯一/null 率/数据集期望）
│   │   │   ├── Rules/              #   规则文件解析、字段值转换（AES/国密/JWT/JSON 展开）与规则生成器
│   │   │   ├── Filter/             #   --where 过滤表达式解析器（词法 + AST）
│   │   │   └── Schema/             #   Schema 快照与演进 Diff
│   │   ├── Parsing/                # 多格式解析：FileParsers 工厂、JSONPath 投影
│   │   │   ├── Parsers/            #   各格式解析器实现（JSON/JSONL/Excel-CSV/XML/YAML/Excel-JSON）
│   │   │   ├── Compression/        #   压缩探测与输入源（gzip/bzip2/zip 直读）
│   │   │   └── Database/           #   数据库直读（PostgreSQL/MySQL/SQLite/SQL Server）
│   │   ├── Reporting/              # 报告导出（JSON/HTML/Markdown/CSV/Excel/PDF/控制台）
│   │   │   ├── Schema/             #   Schema 代码生成（C#/TS/JSON Schema/OpenAPI/Proto/SQL，partial 按方言分文件）
│   │   │   ├── Pii/                #   PII 打码与安全合规报告
│   │   │   └── Quality/            #   质量/违规清单导出
│   │   ├── Security/               # 数据安全：PII 识别、审计日志、KMS 密钥托管（IKmsProvider）
│   ├── ObjectAnalyzer/             # CLI 薄宿主（System.CommandLine 选项解析 + 入口）
│   └── ObjectAnalyzer.Web/         # Blazor Server 交互界面（图表/预览/下载/i18n/主题切换）
│       ├── AnalysisState.cs / LocalizationService.cs   # per-circuit 分析状态 + resx 本地化（中/英）
│       └── Components/Pages/Home.razor                # 单页交互（配置 → 进度 → 结果预览/下载）
├── samples/                        # 示例数据：test_data.*（六种格式）+ 配套规则文件
│   ├── test_rules.json             #   枚举/默认值/主导类型（与 test_data.json 配对）
│   ├── test_*_rules.json           #   值转换/过滤/展开/PII/异常值/数值枚举各专项（与 test_data*.json 成对使用）
│   ├── test_schema_v1/v2/v3.json   #   Schema 快照 v1–v3（配合 --diff-schema 做演进对比）
└── tests/
    ├── ObjectAnalyzer.Tests/       # 核心单元/集成测试
    └── ObjectAnalyzer.Web.Tests/   # Web 冒烟与 P6 功能测试
```

约定：一个顶级类型一个文件、文件名与类型名一致；子目录按功能域分组（对应上表）。
`Security/` 为横向数据安全域，PII 识别与审计日志虽由分析管线调用，源码统一放在此处。

## 构建

```bash
# 还原并构建整个解决方案（CLI + Web + 测试），Release 配置（与 CI 一致）
dotnet build ObjectAnalyzer.sln -c Release
```

构建产物位于各项目的 `bin/` 与 `obj/`，已被 `.gitignore` 忽略。

## CLI 使用方式

```bash
dotnet run --project src/ObjectAnalyzer -- [选项]
```

### 命令行选项

| 选项 | 缩写 | 说明 | 默认值 |
|------|------|------|--------|
| `--input <FILE>` | `-i` | 输入文件路径（JSON / JSONL / CSV / Excel / XML / YAML；支持 .gz / .bz2 / .zip 压缩包直读） | 必填 |
| `--output <FILE>` | `-o` | 输出报告文件路径（`.json` / `.html` / `.md` / `.csv` / `.xlsx` / `.pdf`） | 无 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false`（即扁平化） |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--rules <FILE>` | `-r` | 指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型、枚举与质量检查约束（pattern/范围/必填/唯一/null率 + 数据集期望）、字段值转换及值解析规则 | 无 |
| `--fields <NAMES>` | `-F` | 仅统计这些字段（逗号分隔）；配合规则文件 transform 时，字段值按规则转换后再统计 | 无 |
| `--where <EXPR>` | `-w` | 按字段值过滤对象后再统计（如 `"age >= 25 AND status in ['active']"`）；支持 `== != > < >= <=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感，过滤在字段选择与值转换之后执行 | 无 |
| `--violations <FILE>` | `-v` | 将质量检查违规（枚举/范围/模式/必填/唯一/null 率 + 数据集期望）导出为独立的结构化 JSON 文件，便于 CI 断言 | 无 |
| `--quality <FILE>` | `-q` | 导出结构化数据质量报告 JSON（Pass/Fail 摘要 + 违规率 + 逐字段明细） | 无 |
| `--fail-on-violations` | — | 存在任何质量检查违规（含数据集期望未达标）时以退出码 `3` 结束（质量门禁） | `false` |
| `--sample-reservoir <N>` | — | 蓄水池随机抽样：均匀随机抽取最多 N 个对象分析，快速预估 Schema（如 `--sample-reservoir 50000`）；与 `--sample-step` 互斥 | `0`（关闭） |
| `--sample-step <K>` | — | 等距系统抽样：每 K 条记录取 1 条分析，快速预估 Schema（如 `--sample-step 100`）；与 `--sample-reservoir` 互斥 | `0`（关闭） |
| `--export-schema <FORMAT>[:<FILE>]` | `-s` | 将推断出的数据结构导出为代码定义：`csharp`、`typescript`、`json-schema`、`openapi`、`openapi-yaml`、`proto`，或 SQL `sql`（默认 PostgreSQL）/`postgres`/`mysql`/`sqlite`/`sqlserver`；可带输出路径，如 `-s "csharp:out.cs"`（缺省自动推导） | 无 |
| `--export-schema-snapshot <FILE>` | — | 导出 canonical Schema 快照 JSON（字段名/主导类型/语义/coverage/nullable/范围/枚举/异常值），独立于 `--export-schema` 的代码生成器；作为 schema 演进的基线 | 无 |
| `--diff-schema <BASELINE>` | — | 与历史 Schema 快照（由 `--export-schema-snapshot` 产生）做 diff，输出新增/删除/变更字段；可与 `--schema-diff-report` 配合导出结构化结果 | 无 |
| `--schema-diff-report <FILE>` | — | Schema diff 结果导出到 JSON 文件，便于下游消费（CI 仪表盘 / 评审报告）；未指定时仅在控制台与主报告展示 | 无 |
| `--allow-breaking-schema-changes` | — | Schema 演进门禁：`true`（默认）= 含破坏性变更不触发非零退出码；`false` = 含破坏性变更时以退出码 `6` 结束，与 `--fail-on-violations` / `--fail-on-outlier-ratio` 对称 | `true` |
| `--fail-on-outlier-ratio <RATIO>` | — | CI 异常值门禁：任一数值字段的 IQR 异常值比例（0.0~1.0）超过该阈值时以退出码 `5` 结束；规则文件 `fields.<name>.maxOutlierRatio` 可按字段设置更细粒度阈值 | 无 |
| `--connection <CONN_STR>` | — | 数据库直连：输入连接字符串（如 `"Host=localhost;Database=mydb"`），配合 `--query` 流式读取查询结果，无需先导出为文件 | 无 |
| `--query <SQL>` | — | 数据库直连时执行的 SQL 查询（如 `"SELECT * FROM users"`） | 无 |
| `--provider <DB>` | — | 数据库类型：`postgres`、`mysql`、`sqlite`、`sqlserver`；缺省按连接字符串关键字自动推断（建议显式指定） | 自动推断 |
| `--pii-report <FILE>` | — | 导出数据安全合规等级报告 JSON（合规等级 A/B/C + 敏感字段清单 + 打码样本 + 各类别统计） | 无 |
| `--pii-off` | — | 全局关闭 PII 识别与打码：报告输出原始值（数据确认不含敏感信息或需要完整原文时使用）；与 `--pii-report` 同用时忽略后者，与 `--fail-on-pii-level` 互斥 | `false` |
| `--fail-on-pii-level <A\|B\|C>` | — | CI 合规门禁：合规等级达到或劣于指定级别时以退出码 `4` 结束（`C`=仅 C 级失败；`B`=发现任何敏感字段即失败），与 `--fail-on-violations` 对称 | 无 |
| `--audit-log <PATH>` | — | 数据血缘审计日志：输出 NDJSON 格式事件链（`analysis_start` / `analysis_finish` / `analysis_cancel` / `analysis_error`），每条事件含 `runId` / `operator` / `inputHash` (SHA-256) / `inputBytes` / `options` / `result` / `exitCode`；**不**记录字段值与 AES/SM4/SM2 密钥等敏感材料；同步 AutoFlush 保证审计数据不丢失 | 无 |
| `--operator <ID>` | — | 操作者标识（如 `[email protected]` / `ci-runner-12`），写入审计日志的每条事件（与 `--audit-log` 配合使用） | `anonymous` |
| `--parallel <N>` | `-p` | 指定分析时的最大并行线程数；正整数 (>= 1) 或 `-1`（全部核心） | `-1` |
| `--no-progress` | — | 关闭分析过程中 Spectre.Console 进度条 + ETA，回归到 spinner 行为。典型场景：CI 日志捕获、脚本管道、已重定向到文件时希望避免 ANSI 控制码污染输出 | `false` (默认开) |
| `--checkpoint <PATH>` | — | P6.2：分析完成后将当前结果 + 已处理位置原子写入检查点文件（先 `.tmp` 再 rename），下次可用 `--resume <PATH>` 续跑 | — |
| `--resume <PATH>` | — | P6.2：从检查点续跑。解析器从快照记录的位置之后开始读取新数据，结果与快照在 `FieldStatistic` 级合并（Welford / 直方图 / HLL distinct / 枚举违规 / 时间桶）。源文件路径不一致时拒绝续跑以防错配 | — |
| `--baseline <PATH>` | — | P6.2：合并历史 `AnalysisResult` JSON（`--output ... .json` 产物）。与 `--resume` 互补：本次只分析新增窗口并与历史合并，不需要源文件 | — |
| `--max-memory <MB>` | — | P6.3：进程托管堆上限（MB），仅监控 .NET 托管堆（`GC.GetTotalMemory`）。`0` 表示不监控（默认）。典型场景：50GB JSONL 全量物化会 OOM，设 `--max-memory 1024` 让 `--max-memory-mode soft` 自动启用采样、或者 `--max-memory-mode hard` 立即失败 | `0` |
| `--max-memory-mode <soft\|hard>` | — | P6.3：超限行为（需配合 `--max-memory`）。`soft`（默认）=警告并自动启用 50K 蓄水池采样，分析继续；`hard`=立即以退出码 1 结束 | `soft` |
| `--generate-test-excel` | — | 生成 Excel 测试文件并退出 | — |

> 提示：使用 `-h` 或 `--help` 查看完整帮助。退出码：`0` 成功，`1` 输入/解析错误，`2` 已取消，`3` 质量门禁未通过（`--fail-on-violations`），`4` 合规门禁未通过（`--fail-on-pii-level`），`5` 异常值门禁未通过（`--fail-on-outlier-ratio`），`6` Schema 破坏性变更（`--allow-breaking-schema-changes false` + `--diff-schema`）。

### 示例

#### 基础分析（多格式输入）

```bash
# 分析 JSON 文件
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json

# 分析 CSV 文件并导出 JSON 报告
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.csv -o report.json

# 分析 JSONL 文件，不扁平化，最多展示 10 个唯一值
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.jsonl --no-flatten -m 10

# 分析 XML 文件
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.xml

# 分析 YAML 文件
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.yaml

# 从 Excel 多列 JSON 中分别读取 A / B / C，并保留独立对象桶
# 规则文件示例：samples/test_excel_json_multi_columns_rules.json
# 导出示例：samples/test_data_excel_json_multi_columns.xlsx
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_excel_json_multi_columns.xlsx -r samples/test_excel_json_multi_columns_rules.json

# 生成 Excel 测试文件（含普通 Excel + 单列/多列 JSON 示例）
dotnet run --project src/ObjectAnalyzer -- --generate-test-excel

```

#### 字段选择、转换与过滤

```bash
# 使用规则文件校验枚举约束，并导出 HTML 报告
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_rules.json -o report.html

# 仅统计 name 与 age 字段
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json --fields name,age

# 使用转换规则：解密 / JWT 转明文后统计指定字段
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_transforms.json --fields token,encryptedPhone,encodedName

# 仅统计满足过滤条件的对象（age >= 25 且 status 为 active/vip）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_filter.json --where "age >= 25 AND status in ['active','vip']"

# 通过规则文件配置过滤条件（等价于 --where，CLI 优先级更高）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_filter.json -r samples/test_filter_rules.json

# 解密后展开：base64 结果若为 JSON 对象，展开为 payload.city / payload.age 再统计，并可继续过滤
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_unwrap.json -r samples/test_unwrap_rules.json --where "payload.city == '上海' AND payload.age >= 28"

# 单文件整合：字段选择 + 过滤 + 枚举约束 + 默认值 + 主导类型 + 解密后展开，一次完成
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_all_in_one.json -r samples/test_all_in_one.json
```

#### 报告导出与质量门禁

```bash
# 导出 Markdown 报告 + 独立违规清单（便于 CI 断言）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_rules.json -o report.md --violations violations.json

# 导出数据质量报告 JSON，并启用质量门禁（存在违规则退出码为 3）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_rules.json --quality quality.json --fail-on-violations
```

#### Schema 导出（六种目标）

```bash
# 导出 PostgreSQL CREATE TABLE（缺省方言；嵌套对象/数组折叠为 JSONB 列）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sql:schema.sql"

# 导出 MySQL / SQLite / SQL Server 方言 DDL（反引号标识符 + JSON 列 / INTEGER+TEXT / [方括号]+NVARCHAR(MAX) 等）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "mysql:schema.mysql.sql"
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sqlite:schema.sqlite.sql"
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sqlserver:schema.sqlserver.sql"
```

#### 大文件采样与数据库直连

```bash
# 超大文件采样：等距系统抽样，每 100 条取 1 条（5000W 行 -> 约 50W 条，秒级 Schema 概览）
dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-step 100 -s "csharp:schema.cs"

# 超大文件采样：蓄水池随机抽样，均匀抽取最多 5W 条
dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-reservoir 50000 -s "typescript:schema.d.ts"

# 数据库直连：直连 SQLite 文件并流式分析全表结构与质量
dotnet run --project src/ObjectAnalyzer -- --connection "Data Source=users.db" --query "SELECT * FROM users"

# 数据库直连：PostgreSQL（--provider 显式指定更可靠）；配合 Schema 导出与报告
dotnet run --project src/ObjectAnalyzer -- --provider postgres --connection "Host=localhost;Database=mydb;User Id=app;Password=secret" --query "SELECT * FROM orders" -o orders_report.html -s "sql:orders.sql"

# 超大表直连采样：蓄水池随机抽样 5W 行做 Schema 概览（内存有界）
dotnet run --project src/ObjectAnalyzer -- --provider sqlserver --connection "Server=.;Database=mydb;Trusted_Connection=true" --query "SELECT TOP (1000000) * FROM events" --sample-reservoir 50000 -s "csharp:events.cs"
```

#### JSONPath 投影与敏感数据

```bash
# JSONPath 投影：API 响应 {data: [...], meta: {...}} 只想分析 data 数组
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_wrapped.json --root-path '$.data[*]'

# JSONPath 投影：JSONL 每行 {event, payload} 只想分析 payload
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_payload.jsonl --record-path '$.payload'

# 敏感数据打码 + 导出数据安全合规等级报告（控制台同步输出打码后的值频次）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html --pii-report compliance.json
```

## 安装

> 前置条件：安装 [.NET 10 SDK](#环境要求)。仓库代码无需安装即可通过 `dotnet run` 直接运行（见「快速开始」）；以下为将工具分发给本机或团队的方式。

### 1. 从源码运行（开发 / 一次性分析）

无需安装，在仓库根目录直接运行（首次会自动还原依赖并构建）：

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html
```

### 2. 作为 dotnet tool 安装（本机全局）

```bash
dotnet pack src/ObjectAnalyzer/ObjectAnalyzer.csproj -c Release   # 产出 artifacts/packages/ObjectAnalyzer.Tool.*.nupkg
dotnet tool install --global --add-source artifacts/packages ObjectAnalyzer.Tool
objectanalyzer -i samples/test_data.json -o report.html
```

> 已安装后可用 `dotnet tool uninstall ObjectAnalyzer.Tool` 卸载，`--add-source` 指向本地 `artifacts/packages`；如需发布到 NuGet，将包推送到远端源后省略 `--add-source` 即可。

### 3. Docker 镜像（环境隔离 / 流水线）

```bash
docker build -t objectanalyzer .
# 将数据目录挂载到 /data，输出也写到 /data
docker run --rm -v "$(pwd)/samples:/data" objectanalyzer -i /data/test_data.json -o /data/report.html
```

### 4. Web 界面（本地交互式）

```bash
cd src/ObjectAnalyzer.Web
dotnet run
```

Web 版除支持全部 CLI 选项外，还提供字段覆盖率/类型分布图表、数据预览、报告下载（JSON/HTML/MD/CSV）与 Schema 下载（C#/TS/JSON Schema），并支持中/英文界面切换与浅色/深色主题切换（顶部按钮，选择持久化到 localStorage，首次绘制前即生效，无闪烁）。

## 规则文件格式

```json
{
  "flatten": true,
  "valueParsers": [
    { "source": "A", "parseType": "json", "header": 0 },
    { "source": "B", "parseType": "auto", "header": 0 },
    { "source": "Line", "parseType": "json" },
    { "source": "payload", "parseType": "json-string" }
  ],
  "selectedFields": ["token"],
  "filter": "age >= 25 AND status in ['active', 'vip']",
  "fields": {
    "Gender": {
      "defaultValue": "unknown",
      "primaryType": "String",
      "enumValues": ["Male", "Female", "unknown"]
    },
    "Status": {
      "defaultValue": 0,
      "enumValues": [0, 1, 2]
    },
    "token": {
      "transform": {
        "type": "jwt",
        "claim": "sub"
      }
    },
    "encryptedPhone": {
      "transform": {
        "type": "aes",
        "key": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
        "iv": "YWJjZGVmZ2hpamtsbW5vcA==",
        "mode": "cbc",
        "padding": "pkcs7",
        "format": "base64"
      }
    },
    "encodedName": { "transform": { "type": "base64" } },
    "link": { "transform": { "type": "url" } }
  }
}
```

`valueParsers` 用于声明”值读取时如何解析”，不属于 `fields` 的字段分析配置。`source` 表示来源字段/列名（如 `A` / `B` / `Line` / `payload`），`parseType` 控制是否与如何解析内容：`none` 保持原值、`auto` 自动识别并解析 JSON、`json` 要求值必须是合法 JSON、`json-string` 允许值本身是 JSON 字符串或嵌套字符串 JSON，最终解析出真实对象/数组/值。

`header` 控制 Excel/CSV 表头模式：`1`（默认）首行值为列名，`source` 与字段前缀用表头文字（如 `Line.body.AcctId`）；`0` 无表头，列以字母 `A`/`B`/`C` 命名，`source` 用列字母，首行作为表头跳过。

`flatten` 可以放在顶层规则，也可以直接写在单个 `valueParsers` 项里：顶层值作为全局默认，单项的 `flatten` 允许对某个 source 做单独覆盖；`--no-flatten` 仍然优先级最高。

对于 Excel JSON 多列场景，多个 `valueParsers` 会保持独立对象桶，不会被合并成同一个对象；在 `flatten: true` 时，会展开成 `A.id` / `A.name` / `B.age` 这类字段，结果形态类似 `{“A.id”: 1, “A.name”: “张三”, “B.age”: 28}`。

`selectedFields` 仅统计指定字段（等价于 CLI `--fields`，后者优先级更高）。

`primaryType` 可省略：仅配置 `enumValues` 时自动推导——全部为数值 → `Number`、全部为布尔 → `Boolean`，否则（含混合）→ `String`；显式 `primaryType` 优先。

## KMS 集成（规则文件敏感密钥托管）

规则文件里 `transform` 的敏感密钥字段（`key` / `iv` / `privateKey` / `publicKey` / `signature`）默认需以明文写入 JSON。若不想把真实密钥明文落盘，可用 **KMS 密文引用** 替代：规则文件只存密文，运行时解密注入，明文字符串仅在本次进程内存中存活、不会被写回规则文件。

**密文引用格式**：`kms:<provider>/<version>:<payload>`，例如 `kms:offline/1-base64:T0ExR++170nw+82Snuro...`。不以 `kms:` 前缀开头的值按原样返回（向后兼容既有明文规则文件）。

**可插拔提供方**：工具不内置任何云 SDK 依赖，只定义 `IKmsProvider` 抽象（`ProviderName` + `Decrypt`），运行时按前缀路由到对应提供方——你可以自接任意云 KMS 或自托管实现，只需实现该接口并在启动时注册。

**内置离线提供方 `offline`**（零依赖、开箱即用）：

- 口令从**环境变量 `OA_KMS_PASSPHRASE`** 读取——不落在命令行参数、日志或 shell 历史里。
- 载荷格式 `[magic(4)="OA1G"][salt(16)][nonce(12)][tag(16)][ciphertext]`，AES-256-GCM 认证加密，`SHA-256(passphrase+salt)` 派生密钥（生产可换 PBKDF2/Argon2）。
- `version` 段的 `encoding`（`utf8` / `base64` / `hex`）声明明文字节的还原方式：`base64`/`hex` 类密钥解密后被重新编码为原始文本形式，供 `transform.format` 字段正常消费。

**生成密文引用（CLI）**：

```bash
# 推荐：明文走 stdin，不落 shell 历史
export OA_KMS_PASSPHRASE='你的强口令'
echo -n 'MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=' | dotnet run --project src/ObjectAnalyzer -- kms-secret --encoding base64

# 或将明文放入规则文件 transform 对应字段：
# "key": "kms:offline/1-base64:T0ExR++170nw+82Snuro..."
```

`kms-secret --value <明文> --encoding <utf8|base64|hex>` 亦支持直接传值（不推荐，因明文会出现在 shell 历史）。口令缺失或解密失败（口令错误 / 密文被篡改）时加载规则会 fail-fast。

## 质量检查（规则文件 v2）

除 `enumValues` 枚举约束外，`fields` 中每条字段规则还支持以下质量检查维度：

| 键 | 类型 | 说明 |
|----|------|------|
| `pattern` | string | 正则模式，值不匹配时计为违规（唯一值截断时近似计数并标记） |
| `minValue` / `maxValue` | number | 数值范围越界检查，逐行精确计数，不受唯一值截断影响 |
| `required` | bool | 值为 null 或整行缺失该字段时违规 |
| `unique` | bool | 唯一性检查：未截断时精确统计重复值，截断时基于 HyperLogLog 近似并标记 |
| `nullRateMax` | number | null 率上限（0~1），分子含显式 null 与整行缺失 |
| `maxOutlierRatio` | number | 异常值比例上限（0~1），超过时计为违规；与 `--fail-on-outlier-ratio` 门禁（退出码 5）联动 |

数据集级期望写在顶层 `expectations`：

| 键 | 类型 | 说明 |
|----|------|------|
| `minRowCount` | number | 分析对象总数下限 |
| `maxDuplicateRate` | number | 重复对象占比上限（0~1） |

```json
{
  "fields": {
    "age":    { "minValue": 0, "maxValue": 150, "required": true },
    "email":  { "pattern": "^[^@]+@[^@]+$" },
    "userId": { "unique": true, "nullRateMax": 0.05 }
  },
  "expectations": { "minRowCount": 1000, "maxDuplicateRate": 0.01 }
}
```

所有违规统一汇总到数据质量报告与 `--violations` 导出（含数据集期望条目，枚举违规仍按逐值列出）；配置 `--fail-on-violations` 后存在任一违规即以退出码 3 结束。无效正则会在加载规则文件时立即报错退出（退出码 1）。

`filter` 为全局过滤条件（等价于 CLI `--where`，后者优先级更高），字段名大小写不敏感，应用于字段选择与值转换之后。支持的运算符：

| 运算符 | 说明 | 示例 |
|--------|------|------|
| `==` / `!=` | 等于 / 不等于（数值弱比较，`"20" == 20` 成立） | `age == 20` |
| `>` `<` `>=` `<=` | 数值或字符串比较 | `age >= 18` |
| `in` / `not in` | 属于 / 不属于列表（`[]` 或 `['a','b',1,2]`） | `status in ['active','vip']` |
| `like` / `not like` | 通配符匹配（`%` 任意串、`_` 单字符，大小写不敏感） | `name like '张%'` |
| `AND` / `OR` / 括号 | 逻辑组合（大小写不敏感，`AND` 优先于 `OR`） | `age > 18 AND (gender == '男' OR vip == true)` |

> 缺失字段视为 `null`；`null == null` 成立、`null != 'xx'` 成立。非法表达式在启动时报错退出。

`transform` 支持的转换类型：

| 类型 | 说明 | 参数 |
|------|------|------|
| `jwt` | 将 JWT 解码为 payload 明文（可提取指定 claim） | `claim`（可选，提取 payload 中的某个 claim） |
| `aes` | AES 对称解密，输出 UTF-8 明文 | `key`（必填）、`iv`（CBC 必填）、`mode`（`cbc`/`ecb`）、`padding`（`pkcs7`/`none`）、`format`（`base64`/`hex`，Key 与 IV 统一编码） |
| `base64` | Base64 解码为 UTF-8 字符串 | — |
| `url` | URL 百分号编码解码 | — |

> 转换失败时保留原始值，并在报告中记录该字段的转换失败次数（`TransformFailureCount`）。

所有类型都支持可选参数 `parse`（当前仅 `"json"`）：转换完成后把结果当作 JSON 文本解析并**展开为嵌套字段**再统计——对象按键展开（`payload.city`、`payload.age`）、数组按下标展开（`payload[0]`）；非合法 JSON 时保留转换后的原文。`parse` 也可单独使用（不写 `type`，仅把已是 JSON 文本的字段值展开）。例如一个 base64 加密的 `{"city":"上海","age":30}`，展开后会得到 `payload.city` 与 `payload.age` 两个字段参与统计与过滤。

## 从代码 / Schema 生成规则文件

类结构是权威定义时，可以**静态解析源码或 JSON Schema** 生成规则文件骨架，再用 `-r` 交给分析器做质量校验——「模型优先」的数据校验工作流。仅静态解析，不编译、不参与分析管线（与已移除的 `--type` 编译反序列化是两回事）。

```bash
# 从 C# 类/记录/枚举生成（Java/TS/C++ 等语言建议先用各自生态工具转成 JSON Schema）
dotnet run --project src/ObjectAnalyzer -- rules-from-code --input samples/UserModel.cs -o rules.json

# 从 JSON Schema（draft-07 / 2020-12 常用子集）生成
dotnet run --project src/ObjectAnalyzer -- rules-from-schema --input samples/user_schema.json -o rules.json

# 生成后即可用于分析与质量门禁
dotnet run --project src/ObjectAnalyzer -- -i data.json -r rules.json --fail-on-violations
```

映射能力：

| 规则项 | 来源 |
|--------|------|
| `primaryType` | 声明类型（数值基元 → Number、bool → Boolean、DateTime 系 → DateTime、其余字符串系 → String；JSON Schema `format: date-time/date/time` → DateTime） |
| `enumValues` | 枚举声明成员名（C#）/ `enum` 关键字（Schema） |
| `required` | 非空值类型与 `[Required]`（C#）；`required` 数组（Schema） |
| `minValue` / `maxValue` | `[Range]` / `minimum` + `maximum` |
| `pattern` | `[RegularExpression]` / `pattern` |
| `defaultValue` | 属性初始化器字面量 / `default` |
| 字段键 | `[JsonPropertyName]`（C#）；嵌套对象按扁平化路径展开（`customer.name`） |

边界说明：生成的是**结构约束骨架**——唯一率、null 率、离群率等数据统计类规则无法从代码推导，需按实际数据手工补充；数组字段无法静态确定具体索引，会跳过并在控制台列出；C# 复杂类型（集合/嵌套 POCO）同样跳过；Schema `$ref` 仅解析本地指针（`#/$defs/...`），存在循环引用时报错。

## 自定义 PII 规则（业务敏感字段 + 打码格式）

默认 PII 识别依赖字段语义类型（手机号 / 身份证号 / 银行卡号 / 姓名 / 邮箱）。若你的业务字段（如 `internalId`、`authToken`）不含这些语义，可在规则文件的 `fields` 里通过 `pii` 声明其为敏感字段，并自定义打码格式。声明后，该字段在所有报告输出（JSON / HTML / MD / CSV / 控制台 / `--pii-report`）前都会按规则自动打码。

```json
{
  "fields": {
    "internalId": {
      "pii": {
        "label": "内部标识",
        "highSeverity": false,
        "mask": { "keepPrefix": 2, "keepSuffix": 2 }
      }
    },
    "authToken": {
      "pii": {
        "label": "访问令牌",
        "mask": { "maskAll": true }
      }
    },
    "fullName": {
      "pii": { "mask": { "keepPrefix": 1, "keepSuffix": 1, "maskChar": "#" } }
    }
  }
}
```

字段规则 `pii` 支持：

| 参数 | 说明 | 默认 |
|------|------|------|
| `label` | 合规报告中的分类标签 | `业务敏感字段` |
| `highSeverity` | 是否按高危敏感字段排序 | `true` |
| `mask.keepPrefix` / `mask.keepSuffix` | 保留的前 / 后缀字符数，中间用 `maskChar` 打码 | `0` / `0` |
| `mask.maskChar` | 打码字符 | `*` |
| `mask.maskAll` | 整值打码（忽略 keep 前缀后缀） | `false` |

> 命中自定义规则时优先于内置语义分类；未配置 `mask` 时默认整值打码（`maskAll: true`）。示例见 `samples/test_pii_rules.json`。

## PII 误报逃生口与全局开关

- **字段级白名单 `redact: false`**：字段名启发式可能误伤合法值（如 name 列存的是产品名）。在规则文件中为该字段声明 `"redact": false` 后，即使命中敏感语义/启发式也不打码、不进合规报告。优先级高于 `pii` 自定义规则：

```json
{
  "fields": {
    "name": { "redact": false }
  }
}
```

- **全局开关 `--pii-off`**：数据确认不含敏感信息或需要完整原文时，跳过所有 PII 打码直接输出原始值；与 `--pii-report` 同用时后者被忽略。
- **校验位验证降低误报**：18 位身份证号需通过 GB 11643 MOD 11-2 校验位（15 位一代证仅形态匹配）；16-19 位银行卡号需通过 Luhn 校验；IBAN 需通过 ISO 7064 MOD 97-10 校验。随机数字串不再被误判为敏感证件号。

## CI 合规门禁

`--fail-on-pii-level <A|B|C>` 与 `--fail-on-violations` 对称：分析完成后计算合规等级（A=未发现敏感字段；B=存在敏感字段且已全部打码；C=样本无法完全打码需人工复核），达到或劣于阈值时以退出码 `4` 结束：

```bash
# 仅 C 级（存在未打码泄漏风险）阻断
dotnet run --project src/ObjectAnalyzer -- -i data.json --fail-on-pii-level C

# 发现任何敏感字段即阻断（阈值 B 时 B/C 级均失败）
dotnet run --project src/ObjectAnalyzer -- -i data.json --fail-on-pii-level B
```

## 数据血缘与审计日志

国内合规场景除"敏感数据打码"外，还需要"谁在何时分析了什么"的完整溯源链。`--audit-log` 把每次分析以 **JSON Lines (NDJSON)** 格式追加到指定文件，每行一条事件对象：

| 事件 | 触发时机 | 关键字段 |
| ---- | -------- | -------- |
| `analysis_start` | 通过前置校验后 | `runId` / `operator` / `inputHash` (SHA-256) / `inputBytes` / `options` |
| `analysis_finish` | 分析成功或业务门禁失败 | 上述 + `result`（totalObjects / totalFields / 质量违规 / PII 数） + `exitCode`（0/3/4/5/6） |
| `analysis_cancel` | Ctrl+C 取消 | `exitCode: 2` |
| `analysis_error` | 未捕获异常 | `message`（不含堆栈与字段值） |

**安全设计**：
- **不**记录任何字段值（ValueCounts / Sample / PII 明文），也**不**记录 AES/SM4/SM2 的 `key` / `iv` / `privateKey` / `publicKey` / `signature`；
- transform 字段仅记录 `{field, type}` 摘要用于溯源；
- 同步 AutoFlush + FileShare.Read：进程崩溃不丢数据，外部聚合器可并发读取；
- 审计路径不可写时仅警告，不阻塞分析主流程。

```bash
# 合规留痕：每次分析都写入审计日志，配合 CI runner 标识
dotnet run --project src/ObjectAnalyzer -- \
  -i users-2026-08-26.jsonl \
  --rules rules.json \
  --audit-log /var/log/objectanalyzer/audit.ndjson \
  --operator "[email protected]" \
  --pii-report compliance.json

# 查看最近一次运行的退出码与关键指标
tail -n 2 /var/log/objectanalyzer/audit.ndjson | jq -r '[.event, .operator, .exitCode, .result.totalObjects, .result.piiFieldCount] | @tsv'
```

完整的支持版本、密钥管理、漏洞披露渠道与响应 SLA 见 [SECURITY.md](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/SECURITY.md)。

## 国密算法（SM4 / SM2）

`transform.type` 在原有 `aes` / `jwt` / `base64` / `url` 之外新增两个国密算法类型，适配金融、政务、医疗等国内合规场景：

```jsonc
{
  "fields": {
    // SM4 对称解密（GM/T 0002-2014），CBC + PKCS7，密钥与 IV 均 base64
    "encryptedPayload": {
      "transform": {
        "type": "sm4",
        "mode": "cbc",
        "padding": "pkcs7",
        "format": "base64",
        "key": "MTIzNDU2Nzg5MDEyMzQ1Njc4OTAxMjM=",
        "iv":  "QUJDREVGR0hJSktMTU5PUFFSU1RVVw=="
      }
    },
    // SM2 验签（GM/T 0003-2012，sm2p256v1 曲线）
    "signedMessage": {
      "transform": {
        "type": "sm2",
        "mode": "verify",
        "format": "base64",
        "publicKey": "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoEcz1UBgi0DQgA...\n-----END PUBLIC KEY-----",
        "signature": "MEUCIQCx..."
      }
    }
  }
}
```

| 算法 | 模式 | 必需配置 |
| ---- | ---- | -------- |
| `sm4` | `cbc`（默认） / `ecb` | `key`（16 字节 / base64 或 hex）、`iv`（CBC 必需） |
| `sm2` | `decrypt` | `privateKey`（PEM / Base64 DER / hex 32 字节裸） |
| `sm2` | `verify` | `publicKey`（PEM / Base64 DER / hex 64-65 字节裸）、`signature` |

SM4 测试使用 GM/T 0002-2014 附录 A.1 公开测试向量；SM2 使用 BouncyCastle 现生成密钥对做 round-trip。算法实现见 [FieldTransformProcessor.cs](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Analysis/Rules/FieldTransformProcessor.cs) `ApplySm4` / `ApplySm2`。

## 高级分析能力（P3）

### 日期/时间深度剖析

DateTime / DateTimeOffset 字符串自动归入独立统计通道（不混入数值或文本），统一按 UTC 入桶：

- **解析支持**：`Z` / `+08:00` / 无时区三种形态，无时区值按 UTC 解释
- **输出指标**：`DateTimeCount` / `MinDateTime` / `MaxDateTime` + 时间桶直方图（按数据跨度自适应切档：秒/分/时/天/周/月/年）
- **流式内存**：热路径 O(1) 内存（1 分钟粒度收集，后处理时合并/切档）
- 报告（JSON / HTML / Markdown / 控制台）新增「时间序列」小节

```bash
# 含 ISO-8601 时间戳的 JSONL 自动获得时间序列
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_semantic.json -o report.html
```

### 异常值检测（IQR 1.5×）

数值列复用现有 t-digest 分位数草图计算 P25 / P75 → IQR，识别 1.5×IQR 区间之外的离群点（基于对数直方图近似归类，流式友好）：

- **输出指标**：`OutlierCount` / `OutlierRatio` / `OutlierBounds`（区间文本，如 `[1.5, 95.0]`）
- **小样本保护**：流式样本数 < 4 时不计算，避免抖动
- **质量检查集成**：`fields.<name>.maxOutlierRatio` 字段级阈值；`--fail-on-outlier-ratio <RATIO>` 全局门禁（退出码 5）

```bash
# 示例数据集中混入 IQR 离群点
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_outliers.json -o report.html

# 异常值比例超过 5% 阻断（CI 门禁）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_outliers.json -r samples/test_outlier_rules.json --fail-on-outlier-ratio 0.05
```

### Schema 快照 + Diff

将当前数据集的 Schema 状态导出为 canonical JSON，作为后续演进的"基线"：

```bash
# 1. 首次跑：生成 schema 快照 v1
dotnet run --project src/ObjectAnalyzer -- -i samples/test_schema_v1.json --export-schema-snapshot schema_v1.json

# 2. 数据集 v2 引入新字段、删除旧字段、改类型 —— 导出快照并与 v1 对比
dotnet run --project src/ObjectAnalyzer -- -i samples/test_schema_v2.json --export-schema-snapshot schema_v2.json --diff-schema schema_v1.json --schema-diff-report diff_v1_v2.json

# 3. CI 严格模式：禁止破坏性变更（删除字段、类型变化、coverage 大幅下降、nullable 收紧）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_schema_v3.json --diff-schema schema_v2.json --allow-breaking-schema-changes false
# 含破坏性变更 → 退出码 6
```

`SchemaDiff` 区分**破坏性变更**（字段删除、主导类型变化、nullable 收紧 true→false、coverage 下降 ≥ 0.1）与**非破坏性变更**（coverage 小幅波动、min/max 变化、异常值比例变化等）。`--allow-breaking-schema-changes false` 触发 CI 门禁（退出码 6），与 `--fail-on-violations` / `--fail-on-outlier-ratio` 对称。`--schema-diff-report` 导出结构化 JSON 供仪表盘/评审报告消费。

## 分析深度增强（P4）

P3 落地后，分析能力补齐到数据科学常用维度：跨字段关联、形态归纳、数据卫生、相似识别、时序分析、采样可信度、分布漂移。**零侵入兼容**——任一特性按需开启，关闭时与 v1.x 行为完全一致。所有结果会同时出现在控制台 / JSON / HTML / Markdown 报告与 Blazor Web 界面的 "🧬 P4 深度分析" 区域。

> 这些特性通过 `AnalysisOptions` 与 `AnalysisFeatures` 启用（CLI / Web UI / 库 API 三处一致）；任一开关默认关闭，无任何额外开销。

### 字段相关性矩阵（Pearson / Spearman）

为每个数值字段在流式阶段维护蓄水池（默认 20K 行/字段），分析完成后输出完整 Pearson + Spearman 矩阵与「强相关对」速览（|r| ≥ 0.7 且样本 ≥ 30）。字段数 ≥ 2 且各字段至少 3 个非空数值样本时填充；否则 `CorrelationMatrix` 为 `null`。

```csharp
// 库 API 示例
var opts = new AnalysisOptions { /* ... */ EnableCorrelation = true };
var result = await ObjectAnalyzer.AnalyzeAsync(objects, opts, features: new AnalysisFeatures
{
    EnableCorrelation = true,
    CorrelationReservoir = 20_000
});
// result.CorrelationMatrix.Fields / .Pearson / .Spearman / .StrongPairs
```

### 字符串模式自动归纳

对每个字符串字段将值归一为形态模板（`###-####` / `[A-Z]{3}\d{6}` / `[a-z]+@[a-z]+\.[a-z]+` 等），按字段聚合并输出主导模板 + Top-N 模板 + 模板覆盖率 + 样本数。便于规则文件 `pattern` 编写与异常形态定位。

### Unicode 卫生检查

输出 0~100 卫生评分，识别全/半角混用、零宽字符 / 双向控制符 / BOM 等不可见字符、控制字符、大小写混用、左右引号、不可见分隔符等常见脏数据来源。常见于国际化数据从外部系统灌入时的"看不见的脏"。

### 模糊去重（相似重复识别）

对指定字段（如 `name` / `title` / `address`）做归一化（去空白/标点、全半角统一、大小写折叠）+ 词袋 Jaccard 相似度，将"几乎一致"的对象分组为 `FuzzyDuplicateGroup`：

```csharp
var opts = new AnalysisOptions { FuzzyDedupField = "name" /* 或 title/address */ };
```

### 时间序列深度分析

在 P3「日期/时间深度剖析」时间桶基础上做趋势识别（rising / falling / flat / mixed）、周期检测（日 / 周 / 月）、间隙分析（最大间隔 / 间隔标准差）、脉冲性（孤立 burst 数）。与 `FieldStatistic.DateHistogram` 共用底座，无重复计算。

### 采样置信区间

启用 `--sample-reservoir` / `--sample-step` 时自动启用，对覆盖率与数值均值给出 95% 置信区间，输出"有效样本量"与各字段可信度评估，提示低覆盖字段的统计不确定性。`AnalysisOptions.LowConfidenceWidth` 控制"低可信"判定阈值（默认 0.2）。

### 分布漂移对比（草图 vs 草图）

`DistributionSnapshot` 导出每字段的 t-digest 质心 + HyperLogLog 寄存器（约 10KB/字段，JSON 可序列化、内存有界），`--diff-distribution` 与历史快照对比输出均值 / 分位 / null 率 / distinct / Jaccard 多维度漂移，显著漂移字段用 σ 倍数标记。与 `SchemaDiff` 互补——前者看结构、后者看分布：

```csharp
// 导出基线快照
new AnalysisOptions { DistributionSnapshotPath = "snap_v1.json" };
// 与基线对比
new AnalysisOptions { DiffDistributionBaseline = "snap_v1.json", DistributionDiffReportPath = "diff.json" };
// result.DistributionDiff.SignificantDriftCount / .NumericDrifts / .CategoricalDrifts
```

### Web UI 集成

Blazor 页面 `Home.razor` 的 `.p4-section` 区域以 7 张独立卡片展示（橙色系，与 P3 蓝/绿系区分）：

| 卡片 | 触发条件 | 关键内容 |
|------|----------|----------|
| 🔗 字段相关性矩阵 | `EnableCorrelation=true` 或库调用填充 `CorrelationMatrix` | 字段数 / 样本数 + 强相关对 Top 6 |
| 🔤 字符串模式归纳 | `EnableStringPatterns=true` 或填充 `StringPatterns` | 字段 → 主导模板 + Top 5 模板 |
| 🧹 Unicode 卫生检查 | `EnableUnicodeHygiene=true` 或填充 `UnicodeHygiene` | 卫生评分 + 命中 flag（按字段分项） |
| 👥 模糊去重 | `FuzzyDedupField` 已指定 | 组数 + 相似重复对 + 规范化键 |
| 📈 时间序列深度 | `EnableTimeSeries=true` 或填充 `TimeSeriesAnalyses` | 趋势 + 周期 + 桶数 |
| 🎯 采样置信 | 启用 sampling 时自动计算 | 有效样本量 + 各字段置信区间 |
| 📉 分布漂移 | `DiffDistributionBaseline` 已指定 | 显著漂移字段 + 均值 / σ / Jaccard |

未启用的卡片会显示"无数据"占位（保持 UI 结构稳定），分析完成后浏览器内一键完成 P4 深度分析结果的浏览与对比，无需打开 HTML 报告。

## Web 界面

Web 项目 (`src/ObjectAnalyzer.Web`) 提供浏览器交互式体验，支持上传文件、选择格式、配置所有 CLI 选项（扁平化、类型定义、规则文件、枚举列等），并在界面上展示字段统计、值分布、重复对象、枚举违规、覆盖率/类型分布图表与数据预览；支持报告与 Schema 下载、中/英文界面切换、浅色/深色主题切换。除 P1 基础分析外还内嵌 P3 高级分析（时间序列/异常值/Schema 演进）与 P4 深度分析（相关性矩阵/字符串模式/Unicode 卫生/模糊去重/采样置信/分布漂移）专用卡片小节。

```bash
cd src/ObjectAnalyzer.Web
dotnet run
```

## Excel / PDF 报告导出

在 JSON / HTML / Markdown / CSV 之外，报告导出新增两种表格化格式，方便把分析结果直接交付给非技术受众（业务 / 合规 / 高管），或在 Excel / WPS / LibreOffice 中进一步透视、筛选、生成图表：

- **Excel（.xlsx）**：基于 MiniExcel 多 sheet 工作簿，按数据维度拆分：
  - `摘要` — 总对象数 / 去重数 / 字段数 / 各项 P4 维度计数；
  - `字段统计` — 每个字段的样本数 / 覆盖率 / 类型 / 数值统计 / 长度 / Top 5 值；
  - `重复对象` + `违规清单` — 基础质量信息；
  - `字符串模式` / `Unicode 卫生` / `模糊去重` / `相关性摘要` + `强相关对` + `Pearson 矩阵` + `Spearman 矩阵` / `时间序列深度` / `分布漂移摘要` + `覆盖率变化` + `数值漂移` + `Distinct 漂移` / `采样置信摘要` + `字段覆盖率区间` — 仅在对应 P4 维度有数据时输出对应 sheet。
- **PDF**：基于 QuestPDF A4 排版，包含同样的多章节结构（标题 / 摘要表 / 字段统计表 / 重复对象 / 违规清单 / 全部 P4 小节），自动分页与表头重复，适合打印或邮件附件。

使用方式：

```bash
# CLI：按 -o 文件扩展名自动路由
objectanalyzer -i data.json -o report.xlsx    # → ExcelReportExporter
objectanalyzer -i data.json -o report.pdf     # → PdfReportExporter

# Web：分析完成后在报告区点击"Excel"或"PDF"按钮即时下载
```

CLI 路由在 `ReportExporter.Save` 中实现：检测扩展名后调用对应的 `ExcelReportExporter.Save` / `PdfReportExporter.Save`；Web 端通过 `interop.js` 的 `downloadBinaryFile` 把 base64 编码的二进制流触发为浏览器下载，文件名带时间戳避免冲突。PII 打码与现有所有格式一致——Excel 单元格与 PDF 表格内的敏感字段值在写入前已经过 `PiiRedactor.Redact` 处理。

## JSONPath 投影（仅分析指定节点下的值）

很多业务数据被包在统一外壳里（API 响应 `{data: [...], meta: {...}}`、事件流每行 `{event, payload}`、上游 RPC 返回 `{code, msg, result}` 等），直接分析会被外壳字段污染，也看不到真正的业务字段。`--root-path` 与 `--record-path` 在两个层级应用 JSONPath 表达式（RFC 9535），只把匹配节点作为分析输入：

- `--root-path <EXPR>`：作用于文件根，**仅对整体 JSON 文件生效**。把 `$.data[*]` 这种路径匹配到的节点作为初始记录集。
- `--record-path <EXPR>`：作用于每条记录，**对 JSON / JSONL / CSV / Excel / DB 行均生效**（流式友好）。`$.payload` 把每条记录的 payload 子对象替换为新记录后再分析。
- 两者可独立使用也可组合使用；语法错误硬错误快速失败（退出码 1），匹配空集时仅警告并继续（仍产出报告，便于发现路径配错）。
- Web UI 的"配置"区有对应的两个输入框，配上即可。

```bash
# API 响应 {data: [...], meta: {...}} → 只分析 data 数组
objectanalyzer -i api_response.json --root-path '$.data[*]'

# 事件流 JSONL 每行 {event, payload} → 只分析 payload
objectanalyzer -i events.jsonl --record-path '$.payload'

# 组合：先从根提取数组，再在每条记录上提取子对象
objectanalyzer -i complex.json --root-path '$.events[*]' --record-path '$.details'

# 过滤器：仅分析 active 状态的记录
objectanalyzer -i users.json --root-path '$[?(@.status=="active")]'
```

## 超大文件采样分析

处理 50GB+ 级别的超巨型数据导出时，直接全量统计会消耗大量时间与内存。采样模式只分析数据的一个代表性子集，在数秒内输出准确度达 ~99% 的 Schema 预测概览（字段集合、主导类型、枚举取值与语义模式），常与 `--export-schema` 或报告输出配合使用：

- **`--sample-reservoir <N>` — 蓄水池随机抽样 (Algorithm R)**

  对流式序列在线维护容量为 N 的均匀随机样本：前 N 条直接进入，之后每条以 1/i 概率替换已有样本（i 为已见条数）。内存固定为 N。统计上无偏，适用于行数未知的海量数据。

  ```bash
  dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-reservoir 50000 -s "csharp:schema.cs"
  ```

- **`--sample-step <K>` — 等距系统抽样**

  从第 1 条起每 K 条取 1 条（1st、K+1 条、2K+1 条…），实现最简单、确定性可复现，适用于行数约已知、行间较为一致的数据。

  ```bash
  dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-step 100 -o schema_preview.json
  ```

参数规则：两者互斥（不能同时指定）；`--sample-reservoir` ≥ 2，`--sample-step` ≥ 2；未指定时保持全量分析。

实现层面：`jsonl` / `csv` / `excel` / `excel-json` 等行、记录级流式格式会在**解析前跳过未选中记录**（只付出 I/O 成本，不付出 JSON/单元格解析成本），因此巨型 JSONL 的采样剖析可做到秒级；`json` / `xml` / `yaml` 在整体物化后做统一采样包装。报告中的对象总数与占比均基于样本计算。

## 压缩文件直读

超大文件场景常伴随压缩存储，工具支持 **gzip / bzip2 / zip 压缩包直接作为输入**，无需预先解压：

```bash
# gzip 包裹的 JSONL —— 保持行级流式解压，50GB+ 级别内存有界
objectanalyzer -i huge_export.jsonl.gz --sample-reservoir 50000 -s "csharp:schema.cs"

# bzip2 包裹的 CSV
objectanalyzer -i events.csv.bz2 -o report.html

# zip 压缩包：自动选择数据条目（多数据条目时取字典序首个并在控制台提示）
objectanalyzer -i backup.zip -o report.json

```

识别与处理规则：

- **按扩展名识别**最外层压缩类型（`.gz` / `.gzip` 视同 gzip、`.bz2`、`.zip`），内层格式由剥离压缩后缀后的扩展名决定（如 `data.csv.gz` → CSV、`book.xlsx.gz` → Excel）。
- **扩展名不可判时按头部魔数嗅探**兜底（`1F 8B`=gzip、`BZh`=bzip2、`PK`=zip 容器）；未知扩展名的 Office Open XML 文件（如被改名的 `.xlsx`）会按 Excel 直接解析而非当作 zip 数据容器。
- **zip 条目选择**：跳过目录条目，优先选已知数据格式的条目；多个候选时取全名字典序首个（确定性），其余条目忽略并在控制台提示。zip 内**嵌套 gzip/bzip2 条目**（如 `logs.csv.gz`）会被继续解包。
- **流式透传**：JSONL/CSV/XML/YAML 在解压流上边读边解析，不整体落盘不整体物化；仅内层为需要随机访问的 `.xlsx` 时才将解压内容缓冲到内存。
- 采样（`--sample-reservoir` / `--sample-step`）在解压后的行级流上照常生效。

## 性能基准

> 在 Windows (Release, net10.0) 下测量，数值包含进程启动 + 解析 + 并行分析 + JSON 报告写入的全流程耗时；不同机器/数据分布结果会有差异，仅供量级参考。

| 输入 | 行数/对象数 | 文件大小 | 耗时 |
|------|-------------|----------|------|
| JSONL | 200,000 | 22.5 MB | ~2.6 s |
| JSON（数组） | 200,000 | 11.1 MB | ~2.1 s |
| CSV | 200,000 | 5.5 MB | ~2.6 s |

复现方式：

```bash
# 生成 200k 行 JSONL 并分析
dotnet run --project src/ObjectAnalyzer -- -i /path/to/200k.jsonl -o report.json
```

## 技术栈

- **语言**：C# / .NET 10
- **文件解析**：[MiniExcel](https://github.com/mini-software/MiniExcel)（Excel / CSV）
- **命令行**：[System.CommandLine](https://github.com/dotnet/command-line-api)（beta4）
- **控制台报告**：[Spectre.Console](https://github.com/spectre-console/spectre.console)
- **序列化**：System.Text.Json（内置）
- **XML 解析**：System.Xml.Linq（内置）
- **YAML 解析**：[YamlDotNet](https://github.com/aaubry/YamlDotNet)
- **代码生成**：[Roslyn](https://github.com/dotnet/roslyn)（Microsoft.CodeAnalysis.CSharp，SchemaExporter 的 C# 代码生成使用 SyntaxFactory AST）
- **Web 界面**：Blazor Server (.NET 10)

## 贡献指南

欢迎贡献代码、文档与示例！请先阅读以下约定，确保 PR 能顺利通过 CI。

### 1. 环境准备

- 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。
- 克隆仓库并还原依赖：`dotnet restore ObjectAnalyzer.sln`。

### 2. 本地开发与验证

```bash
# 构建（Release；与 CI 同配置）
dotnet build ObjectAnalyzer.sln -c Release

# 运行 CLI（选项详见「CLI 使用方式」）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json

# 运行全部测试（含覆盖率收集，与 CI 一致）
dotnet test ObjectAnalyzer.sln -c Release --collect:"XPlat Code Coverage"
```

### 3. 代码规范

- **可空引用类型**：`Nullable` 全局启用，新代码必须正确处理 `null`。
- **警告即错误**：`TreatWarningsAsErrors=true`，编译器警告（含未使用变量、可空疏忽）会导致构建失败，请保持零警告。
- **分析级别**：`AnalysisLevel=latest-recommended`，建议开启 IDE 的代码分析提示并修复。
- **格式**：遵循 C# 常规风格（4 空格缩进、文件作用域命名空间、合理使用 `var`），避免与本次改动无关的大规模重构。

### 4. 测试要求

- 测试框架为 **xUnit**，核心用例位于 `tests/ObjectAnalyzer.Tests/`，Web 冒烟用例位于 `tests/ObjectAnalyzer.Web.Tests/`。
- 行为与报告变更应配套**快照 / 回读测试**（现有 `QualityChecksTests`、`PiiTests`、`SamplingTests`、`*ReportExporter` 快照等可作范本）。
- CI 会执行 `dotnet test` + CodeQL 安全扫描，**未通过测试或存在高危告警的 PR 不会被合并**。
- 新增 / 修改功能请在对应 `*Tests.cs` 中补用例；若新增解析器、报告导出器或字段转换，请同步补充单元测试与（如适用）快照数据。

### 5. 提交与变更记录

- **提交信息**使用约定式前缀：`feat:`（新功能）、`fix:`（缺陷修复）、`docs:`、`refactor:`、`test:`、`chore:` 等，正文用中文简述。
- **变更必须同步更新 `CHANGELOG.md`**：在 `[Unreleased]` 下按 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范归类到 `Added` / `Changed` / `Fixed`（中文撰写）。
- **示例数据**：仅 `samples/report*` 被忽略，其余示例输入应随 PR 一并入库（新增功能建议配套样例文件）。

### 6. Pull Request 流程

1. 从主分支切出特性分支（如 `feat/xxx`、`fix/xxx`）。
2. 完成开发与测试，确保本地 `dotnet build` / `dotnet test` 全绿。
3. 推送并发起 PR，描述变更动机、要点与验证方式。
4. 通过 CI（构建、测试、CodeQL）后由维护者评审合并。

> 路线图与产品化优先级见 `ROADMAP.md`；重大设计变更建议先在 Issue / PR 中讨论。

## 许可证

MIT
