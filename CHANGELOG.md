# Changelog

本项目使用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [2.0.0] — 架构重构（破坏性）

### Added
- **API v2 契约：五页面视图响应 + SSE 进度推送（W8）**：同步分析端点（`/api/analyze`、`/api/analyze/raw`、`/api/analyze/config`）统一返回 `AnalyzeViewResponse`——按 React 前端五块页面组织（`overview` 概览统计 / `preview` 数据预览 / `fields` 字段明细 / `deepAnalysis` 深度分析（未启用时省略）/ `quality` 质量报告），并保留 `result`（完整 AnalysisResult）兼容字段；数据预览经管线 `PreviewSink` 捕获转换/过滤后的前 20 条记录。异步作业：`AnalysisJobRegistry` 新增 `ReportProgress` 进度计数与预览承载，快照回显 `processedObjects` 与 `preview`（原始行数组）；新增 `GET /api/analyze/async/{jobId}/events` SSE 事件流（`progress` 500ms 周期 → 终态 `complete` → 关闭；未知作业 `gone`），前端据此后端轮询可逐步迁移到推送。
- **覆盖率拆分显示：值为 null vs 字段不存在**：`FieldStatistic` 新增 `NullCount`（值为显式 null 的记录数，是 `Count` 的子集）——"未覆盖"从此可区分两种原因：值为 null（`nullCount`）与键缺失（`totalObjects - count`）。引擎并行合并 / 增量合并（`MergeFrom`）/ PII 克隆全链路同步该指标；CLI 字段详情显示为 `覆盖率: 2/3 (66.7%)（null 1 · 字段缺失 1）`，Web 字段表格在覆盖率条下方追加 `null N`（琥珀色）与 `字段缺失 N`（红色）标注（中英双语），API 响应的 `fieldStatistics` 自动携带 `nullCount`。质量检查的 required / nullRate 口径不变（null 与缺失均计为坏行）。
- **统一分析管线 `AnalysisPipeline`（W1）**：解析分发（数据库直连 / root-path 整体投影 / 常规文件）→ JSONPath 投影 → 字段选择/转换/过滤 → 采样 → 统计收敛为一条计算路径，CLI / API / Web 三宿主共用；运行期状态（内存预算 / 续跑位置 / 进度桥接 / 预览捕获）经 `AnalysisRunContext` 显式传递。新增"三宿主行为一致性"测试（同输入下管线直调与 CLI 宿主产出逐字节一致的结果 JSON）。
- **宿主端口抽象 `ObjectAnalyzer.Abstractions`**：`IAnalysisConsole` / `IProgressReporter` / `IResultPresenter` + 组合根 `AnalysisServices`——控制台渲染、进度 UI、结果呈现由宿主注入（CLI：Spectre 富渲染；API/Web/测试：静默或桥接），Core 不再耦合任何渲染库。
- **导出注册表（W4）**：`IResultExporter`（`Format` / `CanHandle` / `Save`）+ `ReportExporter` 路由器（html / markdown / csv / excel / pdf 内置 + JSON 兜底）；新增报告格式 = `ReportExporter.Register` 一个实现。
- **门禁注册表（W4）**：`IQualityGate` + `QualityGateRegistry`——数据质量(3) / 数据安全合规(4) / 异常值(5) / Schema 演进(6) 四门禁数据化（退出码与审计摘要均为门禁属性），新增门禁 = 注册新实现。
- **规则单轨（W6）**：`RulesToOptionsConverter.KnownRuntimeKeys` 成为 runtime 键面的单一事实来源，`RulesLint` 白名单改为自动推导；新增 runtime 键 `maxTrackedValues`（引擎唯一值跟踪上限）时白名单同步生效。
- **CLI 新选项 `--max-tracked-values <N>`**（默认 1000）：与分析上限正交化——`--max-values` 明确为"仅控制台展示数量"，不再让人误以为它截断导出结果；规则文件 runtime 节点同步支持 `maxTrackedValues`。
- **API 显式格式覆盖**：`AnalysisOptions.Input.Format`（raw 文本端点场景）；管线解析格式 = 显式覆盖 > 扩展名探测。

### Changed（破坏性）
- **采样顺序统一**：JSON/XML/YAML 等整体物化格式的采样统一移到「字段过滤之后」（样本取自过滤后总体）；JSONL/CSV/Excel 保持解析器内行级采样的流式优化。与 v1.x 相比，"采样 + filter"组合的样本量语义更正确，但结果数值可能与 v1.x 不同。
- **`ObjectAnalyzer.Analyze / AnalyzeDetailed` 新增可选参数 `maxTrackedValues`**（默认 1000，置于 `CancellationToken` 前）；引擎每字段唯一值跟踪上限由编译期常量改为可配置。
- **Core 静态可变状态清零**：删除 `AnalysisRunner` 的 `[ThreadStatic]` 草图缓存与 `s_activeMemoryBudget`（草图经 `PipelineResult` 显式传递）；`AnalysisRunner.RunCore`（440 行）拆为 `Preflight` / `Resume` / `ReportAndGates` 三个 partial 阶段。直接引用这些私有实现的下游代码（不存在于本仓库）需改为公开接口。
- **测试全量并行**：删除 `AnalysisRunnerSequentialGroup` 串行集合（其 AnsiConsole 争用理由随端口化失效），600+ 测试可并行执行。

### Fixed
- **缺陷 2（valueParsers 强制改写格式）**：普通 JSON/JSONL/XML/YAML 配置 `valueParsers` 曾被强制按 excel-json 解析（报"字典键 '1' 缺失"），且 API 侧行为与 CLI 不一致。现仅在电子表格类格式（excel / csv）时切换 excel-json，普通格式按原格式解析；CLI / API / Web 三宿主同源。
- **缺陷 1（filter + flatten 跳过字段级质量检查）**：验证已在管道中修复（范围/枚举/模式检查在过滤+展平后的对象流上正常执行），并以回归测试锁定。

### Removed
- **`ObjectAnalyzer.Core` 移除 Spectre.Console 包依赖**（约 230KB）：145+ 处 `AnsiConsole.*` 调用点全部迁移至宿主端口；Core 现为纯计算库。

## [Unreleased]

### Added
- **API 出站打码关卡（delivery boundary）**：确立「分析内核永远处理原始值，打码是出站边界的职责」——所有结果离开服务前在 `AnalysisCore.AnalyzeFile` 构造 `AnalyzeOutcome` 处统一打码（同步三端点与异步作业同源，新增端点天然继承，结构上不可能再「忘记调用」），结果与数据预览双覆盖。新增 `PiiPolicy`（打码策略一次解析：字段级 `redact:false` 白名单 → 规则 `pii` 声明 → 内置语义类型，作为所有出站形态的唯一判定来源）与 `PreviewRedactor`（预览行按策略打码；数值标量先转不变文化字符串再打码，杜绝数值形态原文出站）；`PiiRedactor` 改为消费策略（`Redact(result, bool)` 重载保留，CLI 四个报告出口零改动）。`AnalysisJobRegistry` 快照与异步 GET 响应同步携带 `piiRedacted` 标记。测试：`PiiPolicyTests`（策略解析优先级 / 空策略直通 / 预览打码）与 `PiiRedactionEndpointTests` 4 个端点契约用例。
- **API 请求级 `piiOff` 开关 + 响应回显 `piiRedacted`**：三个同步端点（multipart 表单 / JSON body / 查询参数）与异步作业统一支持显式关闭出站打码（与 CLI `--pii-off` 对称）；关闭时响应标记 `piiRedacted=false` 并写服务器日志留痕（CLI 侧审计快照本就记录 `piiOff`）。`/api/analyze` 端点文档补充打码行为说明。
- **Mini API 项目 `src/ObjectAnalyzer.Api`（对接 React 前端）**：独立于 Blazor 界面的 HTTP 分析接口，默认监听 `http://localhost:5200`，CORS 已放行 Vite 开发服务器（localhost:5173）：
  - `POST /api/analyze`：multipart 上传数据文件（+ 可选规则文件/内联 rulesJson），支持 flatten / maxValues / fields / filter / 两级 JSONPath / 采样 / 并行度等全部分析参数 → camelCase 完整分析结果；
  - `POST /api/analyze/raw?format=`：请求体即数据文本（JSON/JSONL/CSV…）；
  - `POST /api/rules/validate`：规则 JSON 校验（走 RulesJsonOptions，支持注释）；`GET /api/rules/reference`：返回全量规则参考模板；`GET /api/health`：健康检查。
  - 分析执行复用 Core 管线（FileParsers.ParseByFormat + FieldTransformProcessor 字段选择/转换/过滤 + Sampling + ObjectAnalyzer.Analyze）；新增集成测试项目 `tests/ObjectAnalyzer.Api.Tests`（WebApplicationFactory，8 个用例：健康/原始分析/multipart 上传/缺文件 400/规则校验合法与含注释/参考模板）。
  - 集成 **Scalar 交互式文档**：`GET /scalar`（Scalar 界面，可直接调试）+ `GET /openapi/v1.json`（OpenAPI 3.1），端点已标注摘要与描述；`Microsoft.OpenApi` 显式固定 2.x 规避传递引用的 2.0.0 高危漏洞（NU1903）。
- **全量规则参考模板 `samples/rules_full_reference.jsonc` + 规则加载支持注释**：模板覆盖全部规则配置（flatten / fields 全部 13 项字段规则 / selectedFields / filter / valueParsers / expectations），每一项配置都带中文注释说明用途与取值；AES/国密等需要 KMS 口令的示例默认以块注释给出（取消注释并设置 `OA_KMS_PASSPHRASE` 后启用）。规则加载（CLI LoadRules / Web 粘贴与上传路径 / 审计摘要）统一改用 `AnalysisRules.RulesJsonOptions`（`ReadCommentHandling = Skip` + 大小写不敏感），**带注释的规则文件可直接使用**。测试：`Rules_WithComments_LoadsViaSharedOptions`。
- **配套演示数据 `samples/rules_full_reference_data.json`**（102 行，由 PowerShell 脚本确定性生成）：字段与模板规则一一对应，内置全部违规触发场景——age 必填缺失×2 / 范围越界（200、-3）/ 离群点×6（999）/ 空值×2、gender 非法枚举×3（Robot）、email 正则不匹配×2 与重复值×2、整行重复×2、含可展开的 JWT token（transform: jwt + parse:"json" 演示）与 inactive 行（filter 演示）。端到端验证：模板规则 + 演示数据产出 5 条字段违规（age 3 + email 2），`--fail-on-violations` 退出码 3。⚠ 冒烟中发现已知缺陷：**规则文件同时配置 `filter` 与 `flatten: true` 时字段级质量检查被跳过**（模板中 filter 已注释并标注；Backlog 待修）。
- **Mini API：`POST /api/analyze/raw` 支持规则**：原先该端点硬编码传 `rules: null`，粘贴/直连场景用不了 `fields` 的 transform、枚举、范围校验，更用不了 `valueParsers`。请求体已被数据文本占用，故规则走查询参数 `rulesJson=`（**受请求行长度限制，Kestrel 默认约 8KB；规则较长请改用 multipart 的 `/api/analyze`**），并配套 `allowUnknownRules`；未知键检查与 multipart 版本同源（默认 400 + unknowns）。响应 `AnalyzeRawResponse` 新增可选 `rulesSource`（未传规则时省略，避免前端把「没规则」误判成「规则没生效」）。测试：`AnalyzeRaw_WithRulesJson_AppliesValueParsers` / `AnalyzeRaw_UnknownRuleKey_Returns400` / `AnalyzeRaw_WithoutRules_OmitsRulesSource`。

### Fixed
- **API 分析响应默认出站打码（破坏性行为变更，原为安全缺陷）**：此前 PII 打码只发生在 CLI 报告导出阶段（`PiiRedactor` 仅被 CLI 的 4 个报告出口调用），API 拿到 `AnalysisResult` 后直接序列化返回——配置 PII 规则后响应的值分布 / 重复对象 JSON / 违规样本 / 数据预览全是原始值。现在默认打码生效（含此前完全缺失的数据预览打码）；依赖原始值的消费方需显式传 `piiOff=true`，响应以 `piiRedacted=false` 留痕。
- **打码副本漏抄统计属性（打码字段主导类型回落 "Unknown"）**：`PiiRedactor.CloneWithMask` 原以手工属性清单构造打码副本，漏抄 `PrimaryType`、日期剖析（`dateTimeCount`/`minDateTime`/`maxDateTime`/`dateHistogram`）、异常值四项与 `rangeViolationCount`，且 `histogram` 与原对象共享引用——该缺陷在 CLI 路径早已存在，API 默认打码后全面显形。`FieldStatistic` 新增 `CloneForRedaction()`（`MemberwiseClone` 浅拷贝全部标量统计、可变集合一律换新实例断开共享），`CloneWithMask` 只重填打码相关部分，未来新增属性自动携带。回归测试：`PiiRedactor_MaskedClone_PreservesPrimaryType` / `_PreservesDateTimeAndRangeStats` / `_DetachesMutableCollections`。
- **同步 `/api/analyze/config` 漏传 `format` 给管线**（异步 config 端点有、同步端点没有）：`.txt` 临时文件按扩展名嗅探不出格式即回落 JSONL 解析器，数组型 `dataText` 误报「根节点必须是一个对象」；现与异步端点对齐显式透传 `options.Input.Format`。
- **Mini API 修复「规则里配了 `valueParsers` 却不生效」**：CLI 的 `AnalysisRunner.ResolveEffectiveFormat` 在检测到规则含 `valueParsers` 时会强制切到 `excel-json` 解析器（把 Excel/CSV 单元格里的 JSON 文本解析成对象再展开），但 API 的 `AnalysisCore.AnalyzeFile` 一直直接用扩展名检测出的格式去解析 —— `.xlsx` → `excel`，走的是 `ExcelCsvFileParser`，而该解析器只拿 `valueParsers` 判断表头模式、压根不做 JSON 解析。结果就是整串 JSON 被当成普通文本统计，字段还叫 `A`，规则里的 `fields["A.body"].transform` 永远匹配不上（同一份文件 + 同一份规则，CLI 正常展开、API 全空）。现新增 `AnalysisCore.ResolveEffectiveFormat`（与 CLI 同一套判定），multipart / raw 两条路径统一走它；响应里的 `format` 如实回写实际使用的解析器（`excel-json`），方便前端判断规则是否真的生效。回归测试 `Analyze_ValueParsersRule_ForcesExcelJsonParsing`：CSV 单元格内嵌 JSON + `valueParsers` + `fields["A.body"].transform`，断言能展开出 `A.body.mountain` 且不再残留原始文本列 `A`。
- **检查点文件名清洗跨平台一致化（修复 CI/Linux 失败）**：`P6PostAnalysis.SanitizeCheckpointName` 原使用 `Path.GetInvalidFileNameChars()`，该集合随运行 OS 变化——Linux 上 `<>:"|?*` 与反斜杠均合法，导致同一输入在 GitHub Actions（Linux）与本地（Windows）被清洗成不同结果，并削弱路径穿越防护的一致性。改为跨平台固定字符集（Windows 非法字符 + 正/反斜杠 + 控制字符），`P6PostAnalysisTests` 4 个按 Windows 语义编写的用例现可在任意 OS 通过，并新增 `Sanitize_CrossPlatformRejectsWindowsInvalidChars` 锁定该行为；顺带合并此前误插入的重复 `[Unreleased]` 标题。

### Added
- **从代码 / Schema 生成规则文件（rules-from-code / rules-from-schema 子命令）**：静态解析 C# 源码或 JSON Schema（draft-07 / 2020-12 常用子集）提取类结构，生成规则文件骨架，支撑「模型优先」的数据校验工作流：
  - `RulesFromSchema.Convert`：type → primaryType（`format: date-time/date/time` → DateTime）、enum → enumValues（未声明 type 时按成员类型反推）、required → required、minimum/maximum → minValue/maxValue、pattern → pattern、default → defaultValue；嵌套对象按扁平化路径展开（`customer.name`）；数组字段跳过并记录原因；`$ref` 链式解析本地指针（环路检测 + 32 跳上限，兄弟属性独立守卫支持菱形引用）。
  - `RulesFromCSharp.Extract`（Roslyn SyntaxWalker，仅静态解析不编译）：属性/公共字段声明类型映射、文件内枚举成员 → enumValues、非空值类型与 `[Required]` → required、`[Range]` → 范围、`[RegularExpression]` → pattern、`[JsonPropertyName]` → 字段键、初始化器字面量 → defaultValue；复杂类型（集合/嵌套 POCO）跳过并输出警告；record 与多类文件支持（`--class` 指定目标）。
  - CLI 子命令：`rules-from-code --input <Model.cs> [--class Name] -o rules.json` 与 `rules-from-schema --input <schema.json> -o rules.json`，输出与规则加载器往返兼容（camelCase + 忽略 null），控制台列出跳过项并提示数据统计类规则需手工补充。
  - 测试 16 个用例（`RulesGenerationTests`：类型映射 / 枚举 / 必填 / 约束 / 嵌套展开 / $ref 与循环引用 / 数组跳过 / 往返兼容）；新增 `samples/UserModel.cs` 与 `samples/user_schema.json` 示例。端到端验证：生成规则直接用于 `--fail-on-violations` 门禁（合规退出码 0、违规退出码 3）。

### Removed
- **移除「编译 .cs 类型定义文件后强类型反序列化分析」功能**（原 `--type "File.cs:ClassName"` / `-t`）：删除 `TypeSystem/`（`TypeLoader` Roslyn 运行时编译 + `DynamicAssemblyContext` 可卸载程序集上下文）与 `Parsing/TypedFileParser` / `TypedParseResult`，以及 CLI 选项定义、`AnalysisOptions.TypeFile/TypeName`、AnalysisRunner 的两条强类型分支（含 --resume 不兼容警告）、Web UI 的类型文件上传/目标类名输入与其 resx 键（`OptType*` / `ClearTypeFile` / `ErrTypedParse`）、`PerformanceImprovementsTests` 的沙盒卸载用例与 `TypeLoaderTests`（4 个测试）与 `samples/UserInfo.cs` 示例。Roslyn 包引用保留（`SchemaExporter.CSharp` 的 SyntaxFactory 代码生成仍在使用）。验证：Release 构建 0 警告 0 错误，458 测试全过（405 核心 + 53 Web）。

### Changed
- **`valueParsers.header` 升级为表头行号语义（破坏性）**：旧实现除 `1` 外任何值（含 `3`）一律按 `0` 处理且跳过首行，「header 表示表头所在行」并不成立。现在：`0` = 真正无表头（首行即数据，source 为列字母 A/B/C）；`N`（≥2）= 第 N 行是表头（跳过之前的行，数据从第 N+1 行开始，source 用该行的标题文本）。excel-json 专用解析下**每个 valueParser 独立声明自己的表头行**（同一张表的不同列可有不同表头行）；通用 excel/csv 解析为整表语义（多个 source 声明不一致时取第一个声明的值）。防呆：表头标题找不到 / 行号超出实际行数 / header=0 时 source 非列字母（混合大小写如 `Age`）一律显式报错并给出提示，不再静默跳过。实现上两个 Excel 解析器的 All/Reservoir/Step 三分支收敛为「单核枚举 + 采样准入回调」（未选中行仍不解析 JSON 单元格）。测试：新增 `ValueParserHeaderTests` 6 个用例（多行表头独立绑定 / 标题找不到报错 / 行号越界报错 / 非列字母提示 / CSV 整表重命名 / CSV 走 excel-json 链路）；真实样本（多行表头 xlsx + header 1/0/3 混用规则）端到端验证三个 valueParser 同时生效。
- **`/api/analyze/config` 改为纯 JSON body 全强类型**：请求体直接绑定为强类型 `AnalyzeConfigRequest`——数据文本走 `dataText`（JSON/JSONL/CSV/XML/YAML）内嵌，`format` 显式指定或自动检测，其余全部分析选项（`flatten`/`csvInferNumbers`/`fields`/`filter`/`sample*`/`maxParallel` + P4 开关）与内联 `rules` 规则对象均为强类型字段。OpenAPI / Scalar 现在能看到每个配置字段及其 `rules.*` 子结构（而非一个 JSON 字符串框）。响应改用 `AnalyzeRawResponse`（无文件名）。废弃并删除不再使用的 multipart 表单类型 `AnalyzeConfigForm`。验证：OpenAPI `AnalyzeConfigRequest` schema 列出 `dataText`/`format`/`rules` + 全部分析选项；POST 内嵌 `dataText`+`rules` 返回 200（totalObjects=2）；Api.Tests 17 全绿，构建 0 警告 0 错误。
- **Mini API 出入参 DTO 全部拆成单个文件**：`src/ObjectAnalyzer.Api` 新增 `Contracts/` 目录，把原来集中在 `ApiContracts.cs`（出参）与 `AnalysisCore.cs`（入参）里的所有请求/响应 DTO 各拆一文件——出参：`HealthResponse`、`RulesLintEntry`、`RulesValidationResponse`、`ErrorResponse`、`AnalyzeResponse`、`AnalyzeRawResponse`（+ 响应构造助手 `ApiErrors`）；入参：`AnalyzeForm`、`AnalyzeRequestOptions`、`AnalyzeConfigRequest`、`AnalyzeConfigForm`。`ApiContracts.cs` 删除，`AnalysisCore.cs` 只保留执行核心 `AnalysisCore` 静态类。命名空间 `ObjectAnalyzer.Api` 不变，引用全部跟随归属文件各自补足 `using`。零行为变化；验证：Api.Tests 17 全绿，构建 0 警告 0 错误。
- **Mini API 端点按资源拆成多文件（Minimal API 惯例）**：`src/ObjectAnalyzer.Api/Program.cs` 从 367 行瘦身为启动 + 服务注册 + `MapGroup("/api")` 挂载（约 60 行）；端点按资源拆到 `Endpoints/` 下三个静态扩展类——`HealthEndpoints.MapHealthApi`（/api/health）、`RulesEndpoints.MapRulesApi`（/api/rules/validate + /api/rules/reference）、`AnalyzeEndpoints.MapAnalyzeApi`（/api/analyze + /api/analyze/config + /api/analyze/raw）。每个扩展类以 `IEndpointRouteBuilder` 为入口、`Program.cs` 集中 `.MapXxxApi()` 挂载，路由统一走 `/api` 前缀。`/api/rules/reference` 依赖的 ContentRoot 通过 `IWebHostEnvironment` 注入。零行为变化，路由路径不变；验证：OpenAPI 6 个 `/api/*` 路径全部就位，Api.Tests 17 个全绿，构建 0 警告 0 错误。
- **Mini API 响应改为强类型 DTO（替代匿名对象）**：`src/ObjectAnalyzer.Api` 的 5 个端点去掉 `Results.Ok(new { 匿名 })`，改用具名响应类型（后拆至 `Contracts/`），并用 `Results<Ok<T>, BadRequest<TErr>>` 泛型结果保留状态码——OpenAPI / Scalar 现在为每个接口呈现完整、带字段注释的响应 schema（此前匿名对象在文档里是空的 `{}`）：
  - 新 DTO：`HealthResponse`（/api/health）、`RulesValidationResponse` + `RulesLintEntry`（/api/rules/validate，200/400 同构）、`ErrorResponse`（分析类 400）、`AnalyzeResponse`（/api/analyze）、`AnalyzeRawResponse`（/api/analyze/raw）。
  - `/api/rules/reference` 保持返回原始 `.jsonc` 文本（它会被调用方原样读回再 POST 给 /api/rules/validate 往返，不适合结构化）。
  - 字段注释用属性级 `<summary>`（与 Core 的 `AnalysisResult` 同款，positional record 参数无 XML 注释目标）、JSON 契约 camelCase + 省略 null 不变；`ApiErrors` 助手统一构造错误响应。
  - 验证：OpenAPI 各响应 schema 字段均带 `description`；Api.Tests 13 + 核心 444 + Web 54 全绿；Debug/Release 构建 0 警告 0 错误。**注意**：本机离线构建需 `-p:SignMode=None -p:NuGetAudit=false`（nuget.org 签名校验/漏洞审计不可达导致 NU1301/NU1900）。
- **死代码清理**（168 文件，净删约 780 行，零行为变化）：
  - 未使用 using：临时开启 `GenerateDocumentationFile` + `EnforceCodeStyleInBuild` 后以 `dotnet format style --diagnostics IDE0005` 清除 167 个文件的多余 using（约 760 行），配置随后还原。
  - 未使用私有成员（IDE0051）：删除 `ObjectAnalyzer.MatchesFieldHint`（已被 `ContainsFieldHint` 取代的旧实现）与 `PdfReportExporter.Badge`（无调用的排版助手）。
  - 文档腐烂修复：`NumericSketch` 类注释中指向不存在成员的 `<see cref="LogHistogram"/>` 改为普通文本；`CompressionDetector.SniffByMagic` 补缺失的 `<param name="path">`；7 处悬空 XML 注释归位（CLI 顶级语句本地函数的 `///` 转普通注释，6 个测试类的 `<summary>` 移到 `[Collection]` 特性之前）。
  - 闲置包引用：移除 Web 项目对 MiniExcel 的直接 PackageReference（0 处直接使用，Core 传递依赖仍可用）。
  - 核实为「非死代码」而保留：全部顶级类型均有引用（ErrorSanitizer / P6ProgressReporter 由 Home.razor 使用）；Core 全部 NuGet 包在用；tests 的 SharpZipLib 在用（构造 zip 测试样本）。
  - 验证：Release 构建 0 警告 0 错误（含临时开启的全量 IDE/文档诊断）；462 测试全过。
- **带构造函数的类型改用 C# 12 主构造函数**（12 处转换，构造签名与公共 API 零变化）：`MaxMemoryExceededException`（body-less 简写）、`Hash128` / `FieldFilterToken`（readonly struct，字段初始化器绑定参数）、`ReservoirSampler<T>`、`DecompressingEnumerable`、`FieldCorrelationCollector`（参数 → 字段初始化器）；`HyperLogLog` / `MergeableQuantileSketch`（范围校验抽为静态 `Validate*` 助手，异常类型/消息逐字保留）、`MemoryBudget`（throw 表达式初始化器，声明顺序保证先校验后派生）、`FieldFilter`（解析管线抽为静态 `BuildRoot`）、`AuditLogger`（文件写入器创建抽为静态 `CreateWriter`，初始化器声明顺序保持「先校验路径、后打开文件」）、`DynamicAssemblyContext`（base 调用并入主构造函数）。**刻意不转**：`InputSource`（private 构造 + 静态工厂，主构造函数会放宽为 public）、`FieldTransformProcessor`（构造体为多分支规则装配逻辑）、无构造函数的 DTO 与 record（本就是主构造语义/以对象初始化器使用）。验证：Release 构建 0 警告 0 错误，462 测试全过。
- **核心库目录按功能域重组**（87 个文件纯 `git mv`，命名空间保持不变以避免 Web 端全限定引用连锁修改，零代码改动）：
  - `Analysis/`（88 → 19 文件）：仅保留引擎与运行时（ObjectAnalyzer / AnalysisRunner / Options / Result / Snapshot / Sampling / MemoryBudget 等），新增子目录 `Fields/`（字段统计与草图：FieldStatistic、NumericSketch、HyperLogLog 等 6）、`Insights/`（P4 深度分析：时间序列/字符串模式/相关性/Unicode 卫生/模糊去重共 15）、`Drift/`（分布漂移与快照 Diff 11）、`Quality/`（质量检查 5）、`Rules/`（规则文件与值转换 8）、`Filter/`（--where 表达式解析器与 AST 8）、`Schema/`（Schema 快照与演进 7）。
  - `Security/` 合并为数据安全域（13）：KMS 4 个 + 自 Analysis 迁入的 PII 识别 4 个（PiiDetector/PiiCategory/PiiMaskFormat/PiiValidators）与审计日志 5 个。
  - `Reporting/`（20 → 7）：按格式导出器留根，新增 `Schema/`（SchemaExporter + 6 方言分部）、`Pii/`（打码/合规报告 4）、`Quality/`（质量/违规导出 2）。
  - `Parsing/`（13 → 8）：新增 `Compression/`（压缩探测与输入源 3）、`Database/`（数据库直读 2），`Parsers/` 沿用。
  - 验证：Release 构建 0 警告 0 错误；462 测试全过；git 全部识别为 rename（历史可追溯）。
- **同类嵌套类型/单类多职责文件继续拆分**（与 FileParsers 同模式）：
  - `ObjectAnalyzer.cs`（1422→1205 行）：引擎内 3 个嵌套类型提升为顶级 `internal`——`InternalFieldMetrics`（并行聚合扩展指标）、`ThreadLocalState`→重命名 `AnalyzerThreadState`、`Hash128`（128 位非密码学哈希值类型）。
  - `FieldFilter.cs`（440→251 行）：`--where` 表达式 AST 的 7 个嵌套类型提升为顶级并以 `FieldFilter` 前缀重命名（`FieldFilterTokenKind/Token/Node/LogicalNode/ComparisonNode/CmpOp/Operand`，避免 `Node`/`Token` 泛化名污染 Analysis 命名空间）；求值助手（`TryGetNumber`/`WeakEqual`/`TryGetValueIgnoreCase`/`NumericCompare`/`LikeMatch` 5 个）随唯一消费方 `FieldFilterComparisonNode` 迁移。
  - `SchemaExporter.cs`（1010→210 行）：改为 `partial class` 按方言拆分——`SchemaExporter.CSharp/TypeScript/JsonSchema/OpenApi/Protobuf/Sql` 6 个分部文件（31 个方法归位），公共 API 零变化；核心（`SchemaNode`/`Export`/`BuildSchemaTree`/`ApplyFieldType`/`SanitizeIdentifier`）留在主文件。
  - 刻意不拆：`AnalysisRunner.IncrementalContext`（8 行状态包）、`StringPatternProfiler.TemplateAccumulator`（5 行）——拆分收益为零；纯方法型大文件（HtmlReportExporter 等 4 个）无类型缝隙，属设计级重构另议。
  - 验证：Release 构建 0 警告 0 错误；462 测试全过；Roslyn 复核多类型文件数保持 0。
- **`FileParsers.cs`（1153 行）拆分**：工厂类 `FileParsers` 内的 6 个私有嵌套解析器（JSON / JSONL / Excel-CSV / XML / YAML / Excel-JSON）与惰性解压枚举包装 `DecompressingEnumerable` 提升为顶级 `internal sealed` 类，归入新子目录 `Parsing/Parsers/`（命名空间 `ObjectAnalyzer.Parsing.Parsers`；程序集外公共 API 不变，仅程序集内可见性从 private 放宽到 internal）；约 230 行共享值转换助手（`ParseJsonElement` / `FlattenJson` / `ConvertXElement` / `ConvertYamlDict` / `FlattenObjectDict` 等 9 个）归入同目录新 `internal static class ParserHelpers`，解析器内调用点同步限定为 `ParserHelpers.*`。`FileParsers` 收敛为纯工厂留在 `Parsing/`（118 行：注册表 + 格式探测 + 分发）。Release 构建 0 警告 0 错误，462 测试全过。
- **源码文件按类型拆分**：将 24 个多类型源文件（共 82 个顶级类/枚举/接口/记录）拆分为「一个类型一个文件」，文件名与类型名一致，全部留在原目录；命名空间、编译单元级与命名空间级 using、XML 文档注释逐字保留，纯结构调整零行为变化（Release 构建 0 警告 0 错误，462 测试全过）。原 `CompressedInput.cs` 由 `CompressionType` / `CompressionDetector` / `InputSource` 三个文件取代，其余 23 个主类同名文件（如 `FieldStatistic.cs`）原地收敛为单类型内容。
- **解决方案拆分：抽出核心库 `ObjectAnalyzer.Core`**：将 Analysis / Parsing / Reporting / TypeSystem / Security 五个目录从 CLI 项目（`src/ObjectAnalyzer`）整体迁移到新类库 `src/ObjectAnalyzer.Core`（程序集名 `ObjectAnalyzer.Core`，命名空间保持 `ObjectAnalyzer.*` 不变，公共 API 零变化）。CLI 瘦身为仅含 `Program.cs`（System.CommandLine 选项解析 + 入口渲染）的薄宿主，Blazor Web 与 `ObjectAnalyzer.Tests` 改为直接引用 Core，不再依赖 CLI 可执行项目：
  - Core 承接全部 NuGet 依赖（Roslyn / MiniExcel / QuestPDF / 四类数据库驱动 / BouncyCastle / YamlDotNet / Spectre.Console 等），CLI 仅保留 `System.CommandLine` + `Spectre.Console`；后续可在 Core 内抽象进度/控制台渲染回调（`AnalysisRunner` 目前直接调用 `ConsoleReporter.Print` 与 `AnsiConsole.MarkupLine`），把 Spectre 从核心层彻底剥离——本次未做，保持零行为变化。
  - Dockerfile 还原缓存层同步更新（先 COPY 两个 csproj 再 restore，否则 CLI 的 ProjectReference 还原失败）。
  - dotnet tool 打包验证：nupkg 正常包含 `ObjectAnalyzer.Core.dll`；CI 按解决方案构建、CLI/Web 发布路径不变，无需改动。

### Added
- **KMS 集成（规则文件敏感密钥托管）**：规则文件 JSON 里 `transform` 的敏感密钥字段（`key` / `iv` / `privateKey` / `publicKey` / `signature`）不再必须以明文书写，改为存 KMS 密文引用，运行时解密注入，明文字符串只在本次进程内存中存活、不被写回规则文件：
  - 可插拔抽象：新增 `IKmsProvider`（`ProviderName` + `Decrypt(string ciphertextBase64): byte[]`），`KmsSecretResolver` 按 `kms:<provider>/<version>:<payload>` 前缀路由到对应提供方——不引入云 SDK 依赖，用户可自接任意 KMS / 自托管实现。
  - 内置离线提供方 `OfflineKmsProvider`：口令从环境变量 `OA_KMS_PASSPHRASE` 读取（不落 CLI 参数 / 日志 / shell 历史），载荷格式 `[magic(4)="OA1G"][salt(16)][nonce(12)][tag(16)][ciphertext]`，AES-256-GCM，`SHA-256(passphrase+salt)` 派生密钥（KDF 强度在注释中提示生产可换 PBKDF2/Argon2）。
  - 编码语义自洽往返：`encoding`（`utf8` / `base64` / `hex`）声明明文字节还原方式——`base64`/`hex` 类密钥在解密后被重新编码为原始文本形式，供下游 `format` 字段正常消费（`RuleResolver_DecryptsTransformSensitiveFields` / `RuleResolver_AesEndToEnd_DecryptsValue` 覆盖闭环）。
  - 规则加载自动注入：`AnalysisRunner.LoadRules` 在 pattern 预验证后统一遍历所有 field 的 transform，对敏感字段调用 `KmsRuleResolver.Resolve`；无 `kms:` 前缀的值原样返回（向后兼容既有明文规则文件）。下游 `FieldTransformProcessor.DecodeBytes` 完全无感知。
  - 新 CLI 子命令 `kms-secret`：用 `OA_KMS_PASSPHRASE` 口令加密明文密钥，输出可直接粘贴进规则文件的 `kms:offline/1-<encoding>:<payload>` 引用；明文优先从 stdin 读取（`--value` 可选），避免明文进 shell 历史。
  - 测试：`KmsTests.cs` 10 个用例（AES-GCM 往返、base64/hex 编码还原、salting 语义安全、错误口令 / 缺失口令、前缀解析、未知提供方、规则注入、AES 端到端解密闭环）。
- **P6 Web UI 同步**：把 P6 三块能力（进度条 / 增量分析 / 内存预算）补齐到 Blazor Web 配置面板：
  - 新增 `P6` 可折叠子区段（默认展开），内含 `--no-progress` 取反 / `--max-memory`（MB）/ `--max-memory-mode`（soft|hard）三个数值/选择控件，以及 `--resume` / `--baseline` / `--checkpoint` 三个文件上传入口（与现有 `--type` / `--rules` 复用 `InputFile` + 临时文件模式）。
  - `AnalysisState` 新增 `ShowProgress` / `CheckpointPath/Name` / `ResumeFrom/Name` / `BaselinePath/Name` / `MaxMemoryMB` / `MaxMemoryMode` 六个 P6 字段；`Dispose()` 同步清理新增临时文件。
  - 审计日志 `TryWriteWebAuditLog` 同步 P6 选项到 `AnalysisOptions`（`ShowProgress` / `CheckpointPath` / `ResumeFrom` / `BaselinePath` / `MaxMemoryMB` / `MaxMemoryMode`），确保浏览器端发起的分析在审计 NDJSON 留痕里与 CLI 行为一致。
  - 通用文件上传辅助 `SaveUploadedFileAsync` 复用 64MB 上限与异常隔离（`%TEMP%` 落盘 + 错误时清理）。
  - `app.css` 新增 `.option-group`（details/summary 折叠 + 暗色模式覆盖），与现有 `.options-grid` 视觉对齐。
  - `AppStrings.resx` / `AppStrings.en.resx` 新增 14 个 P6 键（`P6GroupTitle` / `P6GroupHint` / `OptShowProgress` / `OptMaxMemory` / `OptMaxMemoryMode` + soft/hard 选项 / `OptResume` / `OptBaseline` / `OptCheckpoint` / `Clear*`）。
  - `WebSmokeTests` 新增 `HomePage_RendersP6ConfigSection` 断言：折叠区出现 + 关键控件中文标签（同时支持 HTML 实体 / UTF-8 两种编码形式）。`AppStyles_AreServed` 同步断言 `.option-group`。
  - 限制：Web 端不复用 `AnalysisRunner.Run`（其内置 `AnsiConsole.Progress` 与 SPA 浏览器无法观测），故 P6.1 进度条 / P6.3 内存预算目前仅在审计日志中记录选项；用户看到的仍是「点击开始→等待→结果返回」的黑盒体验。`ShowProgress` 字段已为未来 SignalR 实时进度推送预留。
- **P6 选项真正落地到 Web 分析流程**：把进度条 / 增量 / 内存预算 / 检查点从「仅审计日志记录选项」推进到「实际影响本次 run」：
  - P6.1 进度：基于 `IProgress<long>` + `Progress<T>` 自动捕获 `SynchronizationContext` 切回 Blazor UI 线程，把累计对象数写入 `State.ProgressText`（"已分析 12,345 条对象"），UI 展示 spinner + 文本（`aria-live="polite"`）。
  - P6.2 断点续跑 / 基线合并：分析成功路径上调用 `result.MergeFrom(...)`——分别把 `AnalyzerSnapshot.Result`（`--resume`）与 `AnalysisResult` JSON（`--baseline`）合并进本次结果。Web 端不跳读源文件位置（仅合并聚合结果），如需真正从源文件中间续跑请用 CLI；此限制在代码注释中显式说明。
  - P6.2 检查点落盘：分析成功后用 `AnalyzerSnapshot.SaveTo` 把 `Result` 写入 `%TEMP%/objectanalyzer-checkpoints/<safe-name>.snapshot`，文件名通过 `SanitizeCheckpointName` 规整（去路径分隔符 / 控字符 / 长度限制），UI 提供「下载检查点」按钮，下载走新增的 `MapGet("/api/checkpoint/{*path}")` 端点，反规范化 + 边界校验防止路径穿越。
  - P6.3 内存预算：与进度回调同处一个闭包，构造时 `MemoryBudget(maxMemoryMB, mode)`，每个 `progress.Report(count)` 后调用 `memoryBudget.Check(count, out bool samplingAutoEnable)`——soft 模式触发时把 `WarnMemorySoftExceeded` 写到 `State.MemoryWarning`；hard 模式则在分析循环结束后 `ThrowIfHardExceeded()` 抛 `MaxMemoryExceededException`，被 `RunAnalysis` 单独捕获并展示 `ErrMemoryHardExceeded`。
  - 新增 `AnalysisState` 运行时字段：`ProgressText` / `LastCheckpointServerPath` / `LastCheckpointSize` / `MemoryWarning`，均仅由分析循环填充，UI 只读展示；开始新一轮分析时自动重置。
  - `AppStrings.resx` / `AppStrings.en.resx` 新增 9 个运行时键：`ProgressAnalyzedN` / `WarnMemorySoftExceeded` / `ErrMemoryHardExceeded` / `ErrResumeFailed` / `ErrBaselineFailed` / `ErrCheckpointFailed` / `CheckpointReady` / `DownloadCheckpoint`。
  - `app.css` 新增 `.progress-status` / `.memory-warning` / `.checkpoint-info` 三个高亮区 + dark 模式覆盖。
  - `WebSmokeTests` 新增 4 个测试：路径穿越保护（3 个 `InlineData`：URL 编码的 `..`/`..\`/小写）+ 合法路径 200 响应（含正确 `Content-Type: application/json` 与 schema 字段验证）。
- **P6.2 增量分析 / 断点续跑**：解决 50GB 数据中途失败需从头再来的问题，提供三种互补能力：
  - `--checkpoint <PATH>`：本 run 分析完成后将「当前结果 + 已处理位置」原子写入（先 `.tmp` 再 rename，避免半写状态）。快照含 `SourcePath` / `SourceFormat` / `SourcePosition`（行号）/ `Result`（完整 AnalysisResult），schema 版本 `object-analyzer.snapshot/v1`。
  - `--resume <PATH>`：加载快照，校验源文件一致性（防错把 A 文件的快照套到 B 文件上）后，解析器从 `SourcePosition` 跳过后续才读新数据；结果与快照在 `FieldStatistic` 级合并（Welford / 直方图 / HLL distinct / 枚举违规 / 时间桶等均兼容 `MergeFrom`）。
  - `--baseline <PATH>`：合并任意历史 `AnalysisResult` JSON（`--output ... .json` 产物），不需要源文件——适合「本次只分析新增窗口并与历史合并」场景。
  - 合并后报告自动重新覆写 `--output`，确保下游消费者看到 merge 后的最终聚合。
  - 限制：仅 JSONL / CSV / Excel / Excel-JSON 走行级 skip；JSON / XML / YAML 整体物化格式不支持 `--resume`（用户启用时给黄色警告）。数据库直连 `--connection + --query` 不支持行级 skip（DbDataReader 不暴露随机访问）；启用 `--resume` 配 dbMode 时直接 `NotSupportedException` 报错。
  - `AnalyzerSnapshot` 内部类 + `LoadFrom` / `SaveTo` 静态方法 + `AnalysisResult.MergeFrom` 增量合并实现。
  - 10 个 `AnalyzerSnapshotTests` + 4 个 `AnalysisRunnerIncrementalTests` 覆盖：原子写入 / schema 不匹配报错 / 源文件一致性校验 / 行级 skip 实际跳行 / 数值 Welford 合并精度 / 枚举违规合并 / 退出码 0。
- **P6.3 --max-memory 内存预算监控**：解决 50GB JSONL 全量物化 OOM 的问题，提供用户可调托管堆上限：
  - `--max-memory <MB>`：进程托管堆上限（仅监控 .NET 托管堆 `GC.GetTotalMemory`；不监控非托管 / native 内存）。`0` 表示不监控（默认）。
  - `--max-memory-mode soft|hard`（默认 soft）：
    - **soft**：超限仅打印黄色警告并自动启用 50K 蓄水池采样（仅当用户未指定 `--sample-reservoir` / `--sample-step`），让分析在有限内存下继续推进。`AnalysisOptions.MemoryFallbackSampleSize` 控制兜底容量（默认 50,000，足以在 PII / 质量检查场景下保持统计意义，又能将堆控制在 100MB 以内）。
    - **hard**：超限立即以退出码 1 结束（`MaxMemoryExceededException` → AnalysisRunner catch 转为 `analysis_error` 审计事件）。
  - 实现 `MemoryBudget` 类：`Check(objectCount, out bool samplingAutoEnable)` 周期采样；硬模式不在 progress 回调线程上抛异常（会变成"未观察异常"逃出主流程），而是设置 `HardLimitExceeded` 标记，由主流程在分析完成后调用 `ThrowIfHardExceeded` 统一抛错。
  - 入口预检 + spinner/progress 回调每批 Check + 分析完成后 ThrowIfHardExceeded 三重保障——fresh 进程刚启动时 `GC.GetTotalMemory` 可能仅 0.1MB（无任何分配），仅靠入口预检不够；分析循环内每批 Check 由主流程末尾统一抛错。
  - 分析结束打印「[内存] 本次分析峰值 X.X MB（--max-memory 阈值 N MB，模式 Soft/Hard）」一行总结。
  - 10 个 `MemoryBudgetTests` 覆盖：未启用 / soft 模式 autoEnable 单次触发 / hard 模式 ThrowIfHardExceeded 抛异常 / peak 单调非降 / 负数 MB 拒绝 / AnalysisRunner 硬模式小阈值失败 / 充裕阈值成功 / 0 MB 禁用 / 软模式自动启用采样 / 异常消息含阈值。
- **P6.1 进度条 / ETA**：分析过程默认显示 Spectre.Console 进度条，包含 `ProgressBarColumn` / `PercentageColumn` / `RemainingTimeColumn` (ETA) / `SpinnerColumn` / `TaskDescriptionColumn` 五列。`ObjectAnalyzer.Analyze` / `AnalyzeDetailed` 新增 `IProgress<long>?` 回调参数，每分块（10K 对象）触发一次，调用方把累计对象数映射到 task.Value。`--no-progress` CLI 选项可关闭进度条回到旧版 spinner 行为（CI 日志捕获 / 脚本管道场景）。CLI 路由 `--no-progress → AnalysisOptions.ShowProgress = false`。`EstimateTotalObjects` 辅助方法：仅在已物化列表上返回 Count，不对 50GB JSONL 做二次扫描数行。`ProgressReportingTests` 4 个用例验证回调触发次数 / 单调性 / AnalyzeDetailed 兼容性 / 开关标志。
- **Excel / PDF 表格化报告导出**：在现有 JSON / HTML / Markdown / CSV 之外，新增两种适合交付给非技术受众的格式：
  - **Excel（.xlsx）**：基于 [MiniExcel](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer/ObjectAnalyzer.csproj) 的多 sheet 工作簿，按数据维度拆分——`摘要` / `字段统计` / `重复对象` / `违规清单` 基础 4 sheet + P4 七项分析对应的可选 sheet（`字符串模式` / `Unicode 卫生` / `模糊去重` / `相关性摘要` + `强相关对` + `Pearson 矩阵` + `Spearman 矩阵` / `时间序列深度` / `分布漂移摘要` + `覆盖率变化` + `数值漂移` + `Distinct 漂移` / `采样置信摘要` + `字段覆盖率区间`），仅在该维度有数据时输出。字段统计 sheet 包含完整的样本数 / 覆盖率 / 主导类型 / 语义类型 / 数值统计 / 分位数 / 长度 / Top 5 值 / 类型分布等列。PII 打码与现有所有格式一致——敏感字段值在写入单元格前已通过 `PiiRedactor.Redact` 处理。
  - **PDF**：基于 [QuestPDF](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer/ObjectAnalyzer.csproj) 的 A4 结构化排版，包含同样的多章节结构（标题 / 摘要表 / 字段统计表 / 重复对象 / 违规清单 / 全部 P4 小节），自动分页 + 页码 / 总页数 + 表头重复，适合打印或邮件附件交付。
  - **CLI 路由**：[`ReportExporter.Save`](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Core/Reporting/ReportExporter.cs) 检测 `-o` 文件扩展名后调用 `ExcelReportExporter.Save` / `PdfReportExporter.Save`；无需新增 CLI 选项。
  - **Web 下载**：[`Home.razor`](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Web/Components/Pages/Home.razor) 报告区新增 Excel / PDF 下载按钮，通过 [`interop.js`](file:///e:/Code/MyCode/Items/ObjectAnalysisStatistic/src/ObjectAnalyzer.Web/wwwroot/interop.js) 新增的 `downloadBinaryFile` 把 base64 编码的二进制流触发为浏览器下载，文件名带时间戳避免冲突。
- **测试**：新增 `ExcelPdfReportTests` 4 个用例——多 sheet 生成（验证 4 个基础 sheet 名称 + 文件大小 > 1KB）、字段统计 sheet 表头列（`字段名` / `样本数` / `覆盖率`）、PDF 文件头 magic number 校验（`%PDF-`）、`ReportExporter` 按扩展名路由 xlsx / pdf。
- **P4 分析深度增强（七项深度分析能力）**：在 `AnalysisResult` 中新增七类深度分析结果，配合 `AnalysisOptions` 提供的特性开关（`EnableCorrelation` / `EnableStringPatterns` / `EnableUnicodeHygiene` / `EnableTimeSeries` / `FuzzyDedupField` / `DistributionSnapshotPath` / `DiffDistributionBaseline`）按需启用，零侵入兼容默认分析流程：
  - **字段相关性矩阵（Pearson / Spearman）**：流式蓄水池（默认 20K 行/字段）保留每字段数值样本，分析完成后输出完整的 Pearson + Spearman 系数矩阵与「强相关对」（|r| ≥ 0.7 且样本 ≥ 30），帮助发现 `age ↔ income` 这类隐式关联；见 `FieldCorrelationMatrix.cs` / `FieldCorrelationCollector.cs`。
  - **字符串模式自动归纳**：对每个字符串字段自动归纳形态模板（如 `###-####` / `[A-Z]{3}\d{6}` / `[a-z]+@[a-z]+\.[a-z]+`），输出主导模板、Top-N 模板、模板覆盖率与样本数，便于规则文件 pattern 编写与异常形态定位；见 `StringPatternProfiler.cs`。
  - **Unicode 卫生检查**：对每个字符串字段输出卫生评分（0~100），识别全/半角混用、不可见字符（零宽空格 / 双向控制符 / BOM 等）、控制字符、大小写混用、左右引号、不可见分隔符等常见脏数据来源；见 `UnicodeHygieneChecker.cs`。
  - **模糊去重（相似重复识别）**：对指定字段（如 `name` / `title` / `address`）做归一化（去空格/标点/全半角/大小写）+ 词袋 Jaccard 相似度，将"几乎一致"的对象分组为 `FuzzyDuplicateGroup`，输出组数、相似重复对、规范化键；见 `FuzzyDuplicateDetector.cs`。
  - **时间序列深度分析**：对 DateTime 字段在「日期/时间深度剖析」基础上做趋势识别（上升/下降/平稳/混合）、周期检测（日/周/月）、间隙分析（最大间隔 / 间隔标准差）、脉冲性（孤立 burst 数）；见 `TimeSeriesAnalyzer.cs`。
  - **采样置信区间**：启用 `--sample-reservoir` / `--sample-step` 时自动计算，对覆盖率与数值均值给出 95% 置信区间，输出"有效样本量"与各字段的可信度评估，提示低覆盖字段的统计不确定性；见 `Sampling.cs` `ConfidenceInterval`。
  - **分布漂移对比（草图 vs 草图）**：新增 `--export-distribution-snapshot` 导出每字段的 t-digest 质心 + HyperLogLog 寄存器（约 10KB/字段），`--diff-distribution` 与历史快照对比输出均值/分位/null 率/distinct/Jaccard 多维度漂移，显著漂移字段用 σ 倍数标记；与 `SchemaDiff` 互补——前者看结构、后者看分布；见 `DistributionSnapshot.cs`。
- **报告（HTML / Markdown / CSV / JSON）同步**：HTML 与 Markdown 报告新增「字段相关性」「字符串模式」「Unicode 卫生」「模糊去重」「时间序列深度」「采样置信」「分布漂移」共 7 个 P4 专用小节，与现有小节保持一致的视觉风格与可读性。
- **Web UI 同步**：Blazor 页面新增 "🧬 P4 深度分析" 区域，以独立卡片（🔗 字段相关性矩阵 / 🔤 字符串模式归纳 / 🧹 Unicode 卫生检查 / 👥 模糊去重 / 📈 时间序列深度 / 🎯 采样置信 / 📉 分布漂移）形式展示各深度分析结果；AppStrings.resx 同步 40+ P4 专用本地化字符串；app.css 新增 .p4-section/.p4-grid/.p4-card 等样式（橙色系，与 P3 蓝/绿系区分）。
- **测试**：P4 分析深度增强 18 个新增用例——`StringPattern_BuildTemplate_HandlesDigitsAndLetters`、`Correlation_TwoCorrelatedNumericFields_DetectsStrongPair`、`TimeSeries_RisingTrend_DetectedAsRising`、`UnicodeHygiene_FullHalfWidthMix_DetectedAsIssue`、`FuzzyDedup_NearDuplicates_GroupedTogether`、`SamplingConfidence_Reservoir_ProducesInterval`、`DistributionDiff_FieldMeanShifted_ReportedAsDrift` 等核心场景回归；并修复 `FieldCorrelationCollector.MergeFrom()` 流式合并时未克隆 thread-local 数据的 bug 与 `PiiRedactor.Redact()` 丢失 `CorrelationMatrix` / `FuzzyDuplicates` 等 P4 字段的脱敏克隆 bug。
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
