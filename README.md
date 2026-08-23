# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / Excel-JSON 列 / XML / YAML 输入，自动统计字段频次、数据类型、值分布、默认值推断、枚举约束校验及重复对象。
对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / Excel-JSON 列 / XML / YAML / OpenAPI / Protobuf 输入，自动统计字段频次、数据类型、值分布、默认值推断、枚举约束校验及重复对象。

- CLI 命令行工具 (`src/ObjectAnalyzer`) + Blazor 交互式 Web 界面 (`src/ObjectAnalyzer.Web`)
- 多格式支持：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）、XML、YAML、OpenAPI (JSON/YAML)、Protobuf (Binary)
- 规则驱动值解析：通过规则文件 `valueParsers` 指定源字段/列并按 `parseType` 解析 JSON / JSON 字符串，适用于 Excel 多列 JSON 及其他值级解析场景
- 强类型反序列化：指定 .cs 类型定义文件，通过 Roslyn 运行时编译后强类型反序列化 (`--type "File.cs:ClassName"`)
- 字段统计：每个字段的出现频次、覆盖率、主导数据类型
- 值频率分析：展示每个字段中唯一值的出现次数与占比
- 默认值推断：自动推断字段可能的默认值 (`null` / `0` / `false`)
- 枚举约束校验：通过规则文件声明合法枚举值，自动校验并报告违规值
- 重复对象检测：识别并统计完全相同的重复对象（128 位 MD5 哈希）
- 嵌套扁平化：可选将嵌套 JSON 结构扁平化为 `a.b.c` 路径形式
- 多种报告导出：结构化 JSON、富文本 HTML、Markdown、CSV 报告
- 枚举违规独立导出：通过 `--violations` 输出结构化违规清单，便于 CI 断言
- 数据质量报告：`--quality` 输出 Pass/Fail 摘要 + 违规率 + 逐字段明细，`--fail-on-violations` 作为 CI 质量门禁
- Schema 代码导出：`--export-schema csharp|typescript|json-schema|openapi|openapi-yaml|proto|sql` 生成数据结构定义（支持 C# Record, TypeScript, JSON Schema, OpenAPI 3.0 YAML/JSON, gRPC Protobuf 3 `.proto`, 以及 SQL postgres/mysql/sqlite/sqlserver 方言）
- 字段选择与值转换：`--fields` 仅统计指定字段；规则文件 `transform` 支持 JWT 解码 / AES 解密 / Base64 / URL 解码后统计（转换失败自动保留原始值并计数）
- 字段值过滤：`--where` 按字段值过滤对象后再统计（如只统计 `age >= 25 AND status in ['active']`）；支持 `==`、`!=`、`>`、`<`、`>=`、`<=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感，过滤作用于字段选择与值转换之后
- 超大文件采样分析：`--sample-reservoir`（蓄水池随机抽样）与 `--sample-step`（等距系统抽样），在 50GB+ 级别数据导出上数秒内获取准确度达 ~99% 的 Schema 预测概览，内存占用有界
- 数据库直连 (Live DB Ingestion)：`--connection` + `--query` 直连 PostgreSQL / MySQL / SQLite / SQL Server，利用 DbDataReader 惰性流式读取行，无需先导出为 CSV/JSON 即可进行实时结构与质量分析（支持 `--sample-*` 采样与 `-s` Schema 导出）
- 敏感数据识别 (PII) 与打码：自动识别手机号 / 身份证号 / 银行卡号 / 姓名 / 邮箱 / SSN / IBAN / 护照号字段（身份证按 GB 11643 校验位、银行卡按 Luhn、IBAN 按 MOD 97 验证，降低纯形态误报），所有报告输出（JSON / HTML / MD / CSV / 控制台）在导出前按语义自动打码遮蔽（如 `138****1234`、`110101********7528`、`6222********7890`、`张*`、`al***@example.com`），杜绝原始敏感值外泄；规则文件 `fields.<name>.pii` 可将业务敏感字段（如 `internalId`、`authToken`）声明为敏感字段并自定义打码格式，`fields.<name>.redact: false` 白名单可为误报字段（如存产品名的 name 列）豁免打码，`--pii-off` 全局关闭
- 数据安全合规报告：`--pii-report` 输出合规等级（A/B/C）+ 敏感字段清单 + 打码样本 + 各类别统计，用于共享报告前快速判定合规风险；`--fail-on-pii-level <A|B|C>` 提供与 `--fail-on-violations` 对称的 CI 合规门禁
- 转换后对象展开：规则文件 `transform` 支持 `parse: "json"`，将解密/解码结果解析为 JSON 并展开成嵌套字段再统计（如 `payload.city`、`payload[0]`），适用于"解密后对内层对象再次统计"的场景
- 分发方式：`dotnet tool` 打包 (`objectanalyzer`)、Docker 镜像、Blazor Web 界面（含图表/预览/下载/i18n/主题切换）
- 流式解析：JSONL 按行惰性读取 + 分块并行聚合，超大文件内存可控

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本

## 构建

```bash
dotnet build
```

## CLI 使用方式

```bash
dotnet run --project src/ObjectAnalyzer -- [选项]
```

### 命令行选项

| 选项 | 缩写 | 说明 | 默认值 |
|------|------|------|--------|
| `--input <FILE>` | `-i` | 输入文件路径（JSON / JSONL / CSV / Excel / XML / YAML） | 必填 |
| `--output <FILE>` | `-o` | 输出报告文件路径（`.json` / `.html` / `.md` / `.csv`） | 无 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false`（即扁平化） |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--type <FILE>[:<CLASS>]` | `-t` | 指定 .cs 类型定义文件路径，用于强类型反序列化（JSON 根节点支持数组或单个对象）；.cs 含多个类时用冒号指定类名，如 `-t "samples/UserInfo.cs:UserInfo"` | 类名自动检测 |
| `--rules <FILE>` | `-r` | 指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型、枚举约束、字段值转换及值解析规则 | 无 |
| `--fields <NAMES>` | `-F` | 仅统计这些字段（逗号分隔）；配合规则文件 transform 时，字段值按规则转换后再统计 | 无 |
| `--where <EXPR>` | `-w` | 按字段值过滤对象后再统计（如 `"age >= 25 AND status in ['active']"`）；支持 `== != > < >= <=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感，过滤在字段选择与值转换之后执行 | 无 |
| `--violations <FILE>` | `-v` | 将枚举违规导出为独立的结构化 JSON 文件，便于 CI 断言 | 无 |
| `--quality <FILE>` | `-q` | 导出结构化数据质量报告 JSON（Pass/Fail 摘要 + 违规率 + 逐字段明细） | 无 |
| `--fail-on-violations` | — | 存在枚举违规时以退出码 `3` 结束（质量门禁） | `false` |
| `--sample-reservoir <N>` | — | 蓄水池随机抽样：均匀随机抽取最多 N 个对象分析，快速预估 Schema（如 `--sample-reservoir 50000`）；与 `--sample-step` 互斥 | `0`（关闭） |
| `--sample-step <K>` | — | 等距系统抽样：每 K 条记录取 1 条分析，快速预估 Schema（如 `--sample-step 100`）；与 `--sample-reservoir` 互斥 | `0`（关闭） |
| `--export-schema <FORMAT>[:<FILE>]` | `-s` | 将推断出的数据结构导出为代码定义：`csharp`、`typescript`、`json-schema`，或 SQL `sql`（默认 PostgreSQL）/`postgres`/`mysql`/`sqlite`/`sqlserver`；可带输出路径，如 `-s "csharp:out.cs"`（缺省自动推导） | 无 |
| `--connection <CONN_STR>` | — | 数据库直连：输入连接字符串（如 `"Host=localhost;Database=mydb"`），配合 `--query` 流式读取查询结果，无需先导出为文件 | 无 |
| `--query <SQL>` | — | 数据库直连时执行的 SQL 查询（如 `"SELECT * FROM users"`） | 无 |
| `--provider <DB>` | — | 数据库类型：`postgres`、`mysql`、`sqlite`、`sqlserver`；缺省按连接字符串关键字自动推断（建议显式指定） | 自动推断 |
| `--pii-report <FILE>` | — | 导出数据安全合规等级报告 JSON（合规等级 A/B/C + 敏感字段清单 + 打码样本 + 各类别统计） | 无 |
| `--pii-off` | — | 全局关闭 PII 识别与打码：报告输出原始值（数据确认不含敏感信息或需要完整原文时使用）；与 `--pii-report` 同用时忽略后者，与 `--fail-on-pii-level` 互斥 | `false` |
| `--fail-on-pii-level <A\|B\|C>` | — | CI 合规门禁：合规等级达到或劣于指定级别时以退出码 `4` 结束（`C`=仅 C 级失败；`B`=发现任何敏感字段即失败），与 `--fail-on-violations` 对称 | 无 |
| `--parallel <N>` | `-p` | 指定分析时的最大并行线程数；正整数 (>= 1) 或 `-1`（全部核心） | `-1` |
| `--generate-test-excel` | — | 生成 Excel 测试文件并退出 | — |

> 提示：使用 `-h` 或 `--help` 查看完整帮助。退出码：`0` 成功，`1` 输入/解析错误，`2` 已取消，`3` 质量门禁未通过（`--fail-on-violations`），`4` 合规门禁未通过（`--fail-on-pii-level`）。

### 示例

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

# 使用强类型反序列化（.cs 文件含多个类时用冒号指定类名）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -t samples/UserInfo.cs
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -t "samples/UserInfo.cs:UserInfo"

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

# 导出 Markdown 报告 + 独立违规清单（便于 CI 断言）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_rules.json -o report.md --violations violations.json

# 导出数据质量报告 JSON，并启用质量门禁（存在违规则退出码为 3）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -r samples/test_rules.json --quality quality.json --fail-on-violations

# 超大文件采样：等距系统抽样，每 100 条取 1 条（5000W 行 -> 约 50W 条，秒级 Schema 概览）
dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-step 100 -s "csharp:schema.cs"

# 超大文件采样：蓄水池随机抽样，均匀抽取最多 5W 条
dotnet run --project src/ObjectAnalyzer -- -i huge_export.jsonl --sample-reservoir 50000 -s "typescript:schema.d.ts"

# 导出 PostgreSQL CREATE TABLE（缺省方言；嵌套对象/数组折叠为 JSONB 列）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sql:schema.sql"

# 导出 MySQL / SQLite / SQL Server 方言 DDL（反引号标识符 + JSON 列 / INTEGER+TEXT / [方括号]+NVARCHAR(MAX) 等）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "mysql:schema.mysql.sql"
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sqlite:schema.sqlite.sql"
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -s "sqlserver:schema.sqlserver.sql"

# 数据库直连：直连 SQLite 文件并流式分析全表结构与质量
dotnet run --project src/ObjectAnalyzer -- --connection "Data Source=users.db" --query "SELECT * FROM users"

# 数据库直连：PostgreSQL（--provider 显式指定更可靠）；配合 Schema 导出与报告
dotnet run --project src/ObjectAnalyzer -- --provider postgres --connection "Host=localhost;Database=mydb;User Id=app;Password=secret" --query "SELECT * FROM orders" -o orders_report.html -s "sql:orders.sql"

# 超大表直连采样：蓄水池随机抽样 5W 行做 Schema 概览（内存有界）
dotnet run --project src/ObjectAnalyzer -- --provider sqlserver --connection "Server=.;Database=mydb;Trusted_Connection=true" --query "SELECT TOP (1000000) * FROM events" --sample-reservoir 50000 -s "csharp:events.cs"

# 敏感数据打码 + 导出数据安全合规等级报告（控制台同步输出打码后的值频次）
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -o report.html --pii-report compliance.json
```

## 安装与分发

**dotnet tool（NuGet 本地/远端包）**

```bash
dotnet pack src/ObjectAnalyzer/ObjectAnalyzer.csproj -c Release   # 产出 artifacts/packages/ObjectAnalyzer.Tool.*.nupkg
dotnet tool install --global --add-source artifacts/packages ObjectAnalyzer.Tool
objectanalyzer -i samples/test_data.json -o report.html
```

**Docker**

```bash
docker build -t objectanalyzer .
# 将数据目录挂载到 /data，输出也写到 /data
docker run --rm -v "$(pwd)/samples:/data" objectanalyzer -i /data/test_data.json -o /data/report.html
```

**Web 界面**

```bash
cd src/ObjectAnalyzer.Web
dotnet run
```

Web 版除支持全部 CLI 选项外，还提供字段覆盖率/类型分布图表、数据预览、报告下载（JSON/HTML/MD/CSV）与 Schema 下载（C#/TS/JSON Schema），并支持中/英文界面切换与浅色/深色主题切换（顶部按钮，选择持久化到 localStorage，首次绘制前即生效，无闪烁）。

### 规则文件格式

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

### 自定义 PII 规则（业务敏感字段 + 打码格式）

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

### PII 误报逃生口与全局开关

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

### CI 合规门禁

`--fail-on-pii-level <A|B|C>` 与 `--fail-on-violations` 对称：分析完成后计算合规等级（A=未发现敏感字段；B=存在敏感字段且已全部打码；C=样本无法完全打码需人工复核），达到或劣于阈值时以退出码 `4` 结束：

```bash
# 仅 C 级（存在未打码泄漏风险）阻断
dotnet run --project src/ObjectAnalyzer -- -i data.json --fail-on-pii-level C

# 发现任何敏感字段即阻断（阈值 B 时 B/C 级均失败）
dotnet run --project src/ObjectAnalyzer -- -i data.json --fail-on-pii-level B
```

## Web 界面

Web 项目 (`src/ObjectAnalyzer.Web`) 提供浏览器交互式体验，支持上传文件、选择格式、配置所有 CLI 选项（扁平化、类型定义、规则文件、枚举列等），并在界面上展示字段统计、值分布、重复对象、枚举违规、覆盖率/类型分布图表与数据预览；支持报告与 Schema 下载、中/英文界面切换、浅色/深色主题切换。

```bash
cd src/ObjectAnalyzer.Web
dotnet run
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

实现层面：`jsonl` / `csv` / `excel` / `excel-json` 等行、记录级流式格式会在**解析前跳过未选中记录**（只付出 I/O 成本，不付出 JSON/单元格解析成本），因此巨型 JSONL 的采样剖析可做到秒级；`json` / `xml` / `yaml` 与强类型路径在整体物化后做统一采样包装。报告中的对象总数与占比均基于样本计算。

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

## 项目结构

```
ObjectAnalysisStatistic/
├── samples/                          # 示例数据
│   ├── test_data.json / .jsonl / .csv / .xlsx / .xml / .yaml
│   ├── test_data_excel_json.xlsx     # Excel JSON 列模式示例
│   ├── UserInfo.cs                   # 强类型反序列化示例类
│   ├── test_rules.json               # 规则文件示例
│   ├── test_defaults.json            # 默认值规则示例
│   ├── test_enum_violations.json     # 枚举约束违规示例
│   ├── test_numeric_enum.json        # 数值枚举示例
│   ├── test_numeric_enum_rules.json  # 数值枚举规则示例
│   ├── test_transforms.json          # 字段选择 + 值转换规则示例
│   ├── test_data_transforms.json     # 字段转换示例数据
│   ├── test_unwrap_rules.json        # 转换后 JSON 展开规则示例 (parse: json)
│   ├── test_data_unwrap.json         # 解密后对象展开示例数据
│   ├── test_filter_rules.json        # 字段过滤规则示例 (filter)
│   ├── test_data_filter.json         # 字段过滤示例数据
│   ├── test_pii_rules.json           # 自定义 PII 规则示例 (业务敏感字段 + 打码格式)
│   ├── test_data_semantic.json       # 语义推断示例数据 (Email/UUID/IPv4/URL/DateTime)
│   ├── test_all_in_one.json          # 单文件整合示例（选择+过滤+枚举+转换+展开）
│   └── test_data_all_in_one.json     # 整合示例数据
│   （注：samples/report* 为生成物，已被 .gitignore 忽略）
├── src/
│   ├── ObjectAnalyzer/               # CLI 主项目 (net10.0, Exe, dotnet tool)
│   │   ├── Analysis/                 # 分析引擎
│   │   │   ├── AnalysisRunner.cs     # 分析流程编排
│   │   │   ├── ObjectAnalyzer.cs     # 核心分析逻辑 (MapReduce 并行聚合)
│   │   │   ├── FieldRule.cs          # 字段规则 & 枚举约束模型
│   │   │   ├── FieldTransformProcessor.cs # 字段选择 & 值转换 & 过滤 (JWT/AES/Base64/URL)
│   │   │   ├── FieldFilter.cs         # 字段过滤表达式引擎 (--where)
│   │   │   ├── FieldStatistic.cs     # 字段统计数据模型
│   │   │   ├── AnalysisResult.cs     # 分析结果数据模型
│   │   │   ├── DataQualityReport.cs  # 数据质量报告模型 (P1)
│   │   │   ├── PiiDetector.cs        # PII 分类与打码规则
│   │   │   └── DuplicateObjectStatistic.cs
│   │   ├── Parsing/                  # 多格式解析器
│   │   │   ├── FileParsers.cs        # IFileParser 注册表模式
│   │   │   ├── IFileParser.cs        # 解析器接口
│   │   │   ├── JsonSharedOptions.cs  # 共享 JsonSerializerOptions
│   │   │   ├── TestFileGenerator.cs  # 测试数据生成
│   │   │   └── TypedFileParser.cs    # 强类型反序列化
│   │   ├── Reporting/                # 报告输出
│   │   │   ├── ConsoleReporter.cs    # Spectre.Console 控制台报告
│   │   │   ├── HtmlReportExporter.cs # HTML 可视化报告
│   │   │   ├── MarkdownReportExporter.cs # Markdown 报告
│   │   │   ├── CsvReportExporter.cs  # CSV 报告
│   │   │   ├── ViolationExporter.cs  # 枚举违规结构化导出
│   │   │   ├── QualityExporter.cs    # 数据质量报告导出 (P1)
│   │   │   ├── PiiRedactor.cs        # 报告输出前 PII 打码 (redaction)
│   │   │   ├── PiiReport.cs          # 数据安全合规报告模型 (A/B/C)
│   │   │   ├── PiiExporter.cs        # 数据安全合规报告导出
│   │   │   └── ReportExporter.cs     # 报告路由 (按扩展名分发)
│   │   ├── TypeSystem/
│   │   │   └── TypeLoader.cs         # Roslyn 运行时编译 .cs 文件
│   │   └── Program.cs                # System.CommandLine 入口
│   └── ObjectAnalyzer.Web/           # Blazor 交互式 Web 界面 (net10.0)
│       ├── AnalysisState.cs          # 分析状态 & UI 配置
│       ├── LocalizationService.cs    # resx 本地化 (i18n)
│       ├── Resources/AppStrings*.resx  # 中/英文案
│       └── Components/Pages/
├── ObjectAnalyzer.sln
├── Directory.Build.props            # 全局属性 (nullable, warnings as errors)
├── Dockerfile                       # 多阶段 CLI 镜像
├── .github/workflows/ci.yml         # build + test + publish + pack + CodeQL
├── tests/ObjectAnalyzer.Tests/      # xUnit 核心测试 (含快照)
└── tests/ObjectAnalyzer.Web.Tests/  # Web 冒烟 + i18n 测试
```

## 技术栈

- **语言**：C# / .NET 10
- **文件解析**：[MiniExcel](https://github.com/mini-software/MiniExcel)（Excel / CSV）
- **命令行**：[System.CommandLine](https://github.com/dotnet/command-line-api)（beta4）
- **控制台报告**：[Spectre.Console](https://github.com/spectre-console/spectre.console)
- **序列化**：System.Text.Json（内置）
- **XML 解析**：System.Xml.Linq（内置）
- **YAML 解析**：[YamlDotNet](https://github.com/aaubry/YamlDotNet)
- **运行时编译**：[Roslyn](https://github.com/dotnet/roslyn)（Microsoft.CodeAnalysis.CSharp）
- **Web 界面**：Blazor Server (.NET 10)

## 许可证

MIT
