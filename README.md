# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / Excel-JSON 列 / XML / YAML 输入，自动统计字段频次、数据类型、值分布、默认值推断、枚举约束校验及重复对象。

- CLI 命令行工具 (`src/ObjectAnalyzer`) + Blazor 交互式 Web 界面 (`src/ObjectAnalyzer.Web`)
- 多格式支持：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）、XML、YAML
- Excel JSON 列模式：从 Excel 指定列逐行读取 JSON 字符串并解析 (`--format excel-json`)
- 强类型反序列化：指定 .cs 类型定义文件，通过 Roslyn 运行时编译后强类型反序列化 (`--type` / `--type-name`)
- 字段统计：每个字段的出现频次、覆盖率、主导数据类型
- 值频率分析：展示每个字段中不同取值的出现次数与占比
- 默认值推断：自动推断字段可能的默认值 (`null` / `0` / `false`)
- 枚举约束校验：通过规则文件声明合法枚举值，自动校验并报告违规值
- 重复对象检测：识别并统计完全相同的重复对象（128 位 MD5 哈希）
- 嵌套扁平化：可选将嵌套 JSON 结构扁平化为 `a.b.c` 路径形式
- 多种报告导出：结构化 JSON、富文本 HTML、Markdown、CSV 报告
- 枚举违规独立导出：通过 `--violations` 输出结构化违规清单，便于 CI 断言
- 数据质量报告：`--quality` 输出 Pass/Fail 摘要 + 违规率 + 逐字段明细，`--fail-on-violations` 作为 CI 质量门禁
- Schema 代码导出：`--export-schema csharp|typescript|json-schema` 生成数据结构定义
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
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的取值数量 | `5` |
| `--type <FILE>` | `-t` | 指定 .cs 类型定义文件路径，用于强类型反序列化（JSON 根节点支持数组或单个对象） | 无 |
| `--type-name <NAME>` | — | 指定目标类名（当 .cs 文件包含多个类时使用） | 自动检测 |
| `--excel-json-column <COL>` | `-c` | 指定 Excel 中包含 JSON 字符串的列名（A、B、C…），配合 `--format excel-json` | `A` |
| `--rules <FILE>` | `-r` | 指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型和枚举约束 | 无 |
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

# 分析 JSONL 文件，不扁平化，最多展示 10 个取值
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
    }
  }
}
```

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
│   └── test_numeric_enum_rules.json  # 数值枚举规则示例
│   （注：samples/report* 为生成物，已被 .gitignore 忽略）
├── src/
│   ├── ObjectAnalyzer/               # CLI 主项目 (net10.0, Exe, dotnet tool)
│   │   ├── Analysis/                 # 分析引擎
│   │   │   ├── AnalysisRunner.cs     # 分析流程编排
│   │   │   ├── ObjectAnalyzer.cs     # 核心分析逻辑 (MapReduce 并行聚合)
│   │   │   ├── FieldRule.cs          # 字段规则 & 枚举约束模型
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
