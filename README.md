# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / Excel-JSON 列 / XML / YAML 输入，自动统计字段频次、数据类型、值分布、默认值推断、枚举约束校验及重复对象。

- CLI 命令行工具 (`src/ObjectAnalyzer`) + Blazor 交互式 Web 界面 (`src/ObjectAnalyzer.Web`)
- 多格式支持：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）、XML、YAML
- Excel JSON 列模式：从 Excel 指定列逐行读取 JSON 字符串并解析 (`--format excel-json`)
- 强类型反序列化：指定 .cs 类型定义文件，通过 Roslyn 运行时编译后强类型反序列化 (`--type` / `--type-name`)
- 字段统计：每个字段的出现频次、覆盖率、主导数据类型
- 值频率分析：展示每个字段中唯一值的出现次数与占比
- 默认值推断：自动推断字段可能的默认值 (`null` / `0` / `false`)
- 枚举约束校验：通过规则文件声明合法枚举值，自动校验并报告违规值
- 重复对象检测：识别并统计完全相同的重复对象（128 位 MD5 哈希）
- 嵌套扁平化：可选将嵌套 JSON 结构扁平化为 `a.b.c` 路径形式
- 多种报告导出：结构化 JSON、富文本 HTML、Markdown、CSV 报告
- 枚举违规独立导出：通过 `--violations` 输出结构化违规清单，便于 CI 断言
- 数据质量报告：`--quality` 输出 Pass/Fail 摘要 + 违规率 + 逐字段明细，`--fail-on-violations` 作为 CI 质量门禁
- Schema 代码导出：`--export-schema csharp|typescript|json-schema` 生成数据结构定义
- 字段选择与值转换：`--fields` 仅统计指定字段；规则文件 `transform` 支持 JWT 解码 / AES 解密 / Base64 / URL 解码后统计（转换失败自动保留原始值并计数）
- 字段值过滤：`--where` 按字段值过滤对象后再统计（如只统计 `age >= 25 AND status in ['active']`）；支持 `==`、`!=`、`>`、`<`、`>=`、`<=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感，过滤作用于字段选择与值转换之后
- 转换后对象展开：规则文件 `transform` 支持 `parse: "json"`，将解密/解码结果解析为 JSON 并展开成嵌套字段再统计（如 `payload.city`、`payload[0]`），适用于"解密后对内层对象再次统计"的场景
- 分发方式：`dotnet tool` 打包 (`objectanalyzer`)、Docker 镜像、Blazor Web 界面（含图表/预览/下载/i18n）
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
| `--format <FORMAT>` | `-f` | 显式指定输入格式：`json`、`jsonl`、`csv`、`excel`、`excel-json`、`xml`、`yaml` | 自动检测 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false`（即扁平化） |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--type <FILE>` | `-t` | 指定 .cs 类型定义文件路径，用于强类型反序列化（JSON 根节点支持数组或单个对象） | 无 |
| `--type-name <NAME>` | — | 指定目标类名（当 .cs 文件包含多个类时使用） | 自动检测 |
| `--excel-json-column <COL>` | `-c` | 指定 Excel 中包含 JSON 字符串的列名（A、B、C…），配合 `--format excel-json` | `A` |
| `--rules <FILE>` | `-r` | 指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型、枚举约束和字段值转换 | 无 |
| `--fields <NAMES>` | — | 仅统计这些字段（逗号分隔）；配合规则文件 transform 时，字段值按规则转换后再统计 | 无 |
| `--where <EXPR>` | — | 按字段值过滤对象后再统计（如 `"age >= 25 AND status in ['active']"`）；支持 `== != > < >= <=`、`in`/`not in`、`like`/`not like`、`AND`/`OR` 与括号，字段名大小写不敏感，过滤在字段选择与值转换之后执行 | 无 |
| `--violations <FILE>` | — | 将枚举违规导出为独立的结构化 JSON 文件，便于 CI 断言 | 无 |
| `--quality <FILE>` | — | 导出结构化数据质量报告 JSON（Pass/Fail 摘要 + 违规率 + 逐字段明细） | 无 |
| `--fail-on-violations` | — | 存在枚举违规时以退出码 `3` 结束（质量门禁） | `false` |
| `--export-schema <FORMAT>` | `-s` | 将推断出的数据结构导出为代码定义：`csharp`、`typescript`、`json-schema` | 无 |
| `--schema-output <FILE>` | — | Schema 代码导出的保存路径（配合 `--export-schema`） | 无 |
| `--parallel <N>` | `-p` | 指定分析时的最大并行线程数；正整数 (>= 1) 或 `-1`（全部核心） | `-1` |
| `--generate-test-excel` | — | 生成 Excel 测试文件并退出 | — |

> 提示：使用 `-h` 或 `--help` 查看完整帮助。退出码：`0` 成功，`1` 输入/解析错误，`2` 已取消，`3` 质量门禁未通过（`--fail-on-violations`）。

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

# 从 Excel A 列读取 JSON 字符串
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_excel_json.xlsx -f excel-json -c A

# 使用强类型反序列化
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -t samples/UserInfo.cs

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

Web 版除支持全部 CLI 选项外，还提供字段覆盖率/类型分布图表、数据预览、报告下载（JSON/HTML/MD/CSV）与 Schema 下载（C#/TS/JSON Schema），并支持中/英文界面切换。

### 规则文件格式

```json
{
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
      "primaryType": "Number",
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
        "keyFormat": "base64",
        "ivFormat": "base64"
      }
    },
    "encodedName": { "transform": { "type": "base64" } },
    "link": { "transform": { "type": "url" } }
  }
}
```

`selectedFields` 仅统计指定字段（等价于 CLI `--fields`，后者优先级更高）。

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
| `aes` | AES 对称解密，输出 UTF-8 明文 | `key`（必填）、`iv`（CBC 必填）、`mode`（`cbc`/`ecb`）、`padding`（`pkcs7`/`none`）、`keyFormat`/`ivFormat`（`base64`/`hex`） |
| `base64` | Base64 解码为 UTF-8 字符串 | — |
| `url` | URL 百分号编码解码 | — |

> 转换失败时保留原始值，并在报告中记录该字段的转换失败次数（`TransformFailureCount`）。

所有类型都支持可选参数 `parse`（当前仅 `"json"`）：转换完成后把结果当作 JSON 文本解析并**展开为嵌套字段**再统计——对象按键展开（`payload.city`、`payload.age`）、数组按下标展开（`payload[0]`）；非合法 JSON 时保留转换后的原文。`parse` 也可单独使用（不写 `type`，仅把已是 JSON 文本的字段值展开）。例如一个 base64 加密的 `{"city":"上海","age":30}`，展开后会得到 `payload.city` 与 `payload.age` 两个字段参与统计与过滤。

## Web 界面

Web 项目 (`src/ObjectAnalyzer.Web`) 提供浏览器交互式体验，支持上传文件、选择格式、配置所有 CLI 选项（扁平化、类型定义、规则文件、枚举列等），并在界面上展示字段统计、值分布、重复对象、枚举违规、覆盖率/类型分布图表与数据预览；支持报告与 Schema 下载、中/英文界面切换。

```bash
cd src/ObjectAnalyzer.Web
dotnet run
```

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
