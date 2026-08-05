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
- 多种报告导出：结构化 JSON、富文本 HTML 可视化报告

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
| `--output <FILE>` | `-o` | 输出报告文件路径（`.json` 或 `.html`） | 无 |
| `--format <FORMAT>` | `-f` | 显式指定输入格式：`json`、`jsonl`、`csv`、`excel`、`excel-json`、`xml`、`yaml` | 自动检测 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false`（即扁平化） |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--type <FILE>` | `-t` | 指定 .cs 类型定义文件路径，用于强类型反序列化 | 无 |
| `--type-name <NAME>` | — | 指定目标类名（当 .cs 文件包含多个类时使用） | 自动检测 |
| `--excel-json-column <COL>` | `-c` | 指定 Excel 中包含 JSON 字符串的列名（A、B、C…），配合 `--format excel-json` | `A` |
| `--rules <FILE>` | `-r` | 指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型和枚举约束 | 无 |
| `--generate-test-excel` | — | 生成 Excel 测试文件并退出 | — |

> 提示：使用 `-h` 或 `--help` 查看完整帮助。

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
```

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

Web 项目 (`src/ObjectAnalyzer.Web`) 提供浏览器交互式体验，支持上传文件、选择格式、配置所有 CLI 选项（扁平化、类型定义、规则文件、枚举列等），并在界面上展示字段统计、值分布、重复对象和枚举违规。

```bash
cd src/ObjectAnalyzer.Web
dotnet run
```

## 项目结构

```
ObjectAnalysisStatistic/
├── samples/                          # 示例数据与报告
│   ├── test_data.json / .jsonl / .csv / .xlsx / .xml / .yaml
│   ├── test_data_excel_json.xlsx     # Excel JSON 列模式示例
│   ├── UserInfo.cs                   # 强类型反序列化示例类
│   ├── test_rules.json               # 规则文件示例
│   └── report*.json / report*.html   # 示例报告输出
├── src/
│   ├── ObjectAnalyzer/               # CLI 主项目 (net10.0, Exe)
│   │   ├── Analysis/                 # 分析引擎
│   │   │   ├── AnalysisRunner.cs     # 分析流程编排
│   │   │   ├── ObjectAnalyzer.cs     # 核心分析逻辑 (MapReduce 并行聚合)
│   │   │   ├── FieldRule.cs          # 字段规则 & 枚举约束模型
│   │   │   ├── FieldStatistic.cs     # 字段统计数据模型
│   │   │   ├── AnalysisResult.cs     # 分析结果数据模型
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
│   │   │   └── ReportExporter.cs     # JSON 报告导出
│   │   ├── TypeSystem/
│   │   │   └── TypeLoader.cs         # Roslyn 运行时编译 .cs 文件
│   │   └── Program.cs                # System.CommandLine 入口
│   └── ObjectAnalyzer.Web/           # Blazor 交互式 Web 界面 (net10.0)
│       ├── AnalysisState.cs          # 分析状态 & UI 配置
│       └── Components/Pages/
├── ObjectAnalyzer.sln
├── Directory.Build.props            # 全局属性 (nullable, warnings as errors)
└── tests/ObjectAnalyzer.Tests/      # xUnit 测试
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
