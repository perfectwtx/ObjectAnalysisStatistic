# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel / XML / YAML 输入，自动统计字段频次、数据类型、值分布及重复对象。

## 功能特性

- **多格式支持**：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）、XML、YAML
- **Excel JSON 列模式**：从 Excel 指定列逐行读取 JSON 字符串并解析
- **强类型反序列化**：指定 .cs 类型定义文件，通过 Roslyn 运行时编译后强类型反序列化
- **字段统计**：每个字段的出现频次、覆盖率、数据类型分布
- **值频率分析**：展示每个字段中唯一值的出现次数与占比
- **默认值推断**：自动推断字段可能的默认值（`null` / `0` / `false`）
- **重复对象检测**：识别并统计完全相同的重复对象（128 位 MD5 哈希）
- **嵌套扁平化**：可选将嵌套 JSON 结构扁平化为 `a.b.c` 路径形式
- **JSON 报告导出**：将完整分析结果保存为结构化 JSON 文件

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本

## 构建

```bash
dotnet build
```

## 使用方式

```bash
dotnet run --project src/ObjectAnalyzer -- [选项]
```

### 命令行选项

| 选项 | 缩写 | 说明 | 默认值 |
|------|------|------|--------|
| `--input <FILE>` | `-i` | 输入文件路径（JSON / JSONL / CSV / Excel / XML / YAML） | 必填 |
| `--output <FILE>` | `-o` | 输出报告文件路径（.json） | 无 |
| `--format <FORMAT>` | `-f` | 显式指定输入格式：`json`、`jsonl`、`csv`、`excel`、`xml`、`yaml`、`excel-json` | 自动检测 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false` |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--type <FILE>` | `-t` | 指定 .cs 类型定义文件路径，用于强类型反序列化 | 无 |
| `--type-name <NAME>` | — | 指定目标类名（当 .cs 文件包含多个类时使用） | 自动检测 |
| `--excel-json-column <COL>` | `-c` | 指定 Excel 中包含 JSON 字符串的列名（A、B、C…），配合 `--format excel-json` | `A` |
| `--generate-test-excel` | — | 生成 Excel 测试文件并退出 | — |

### 示例

**分析 JSON 文件：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

**分析 CSV 文件并导出 JSON 报告：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.csv -o report.json
```

**分析 JSONL 文件，不扁平化，最多展示 10 个唯一值：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.jsonl --no-flatten -m 10
```

**分析 XML 文件：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.xml
```

**分析 YAML 文件：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.yaml
```

**从 Excel A 列读取 JSON 字符串：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data_excel_json.xlsx -f excel-json -c A
```

**使用强类型反序列化：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json -t samples/UserInfo.cs
```

## 项目结构

```
ObjectAnalysisStatistic/
├── samples/                          # 示例数据与报告
│   ├── test_data.json
│   ├── test_data.jsonl
│   ├── test_data.csv
│   ├── test_data.xlsx
│   ├── test_data_excel_json.xlsx
│   ├── test_data.xml
│   ├── test_data.yaml
│   └── UserInfo.cs                   # 强类型反序列化示例类
├── src/
│   └── ObjectAnalyzer/               # 主项目
│       ├── Program.cs                # 入口 & 命令行参数定义
│       ├── AnalysisOptions.cs        # 分析选项封装
│       ├── AnalysisRunner.cs         # 分析流程编排
│       ├── ObjectAnalyzer.cs         # 核心分析逻辑（并行聚合）
│       ├── FileParsers.cs            # 多格式文件解析器
│       ├── ConsoleReporter.cs        # 控制台报告输出
│       ├── ReportExporter.cs         # JSON 报告导出
│       ├── TypeLoader.cs             # Roslyn 运行时编译 .cs 文件
│       ├── TypedFileParser.cs        # 强类型反序列化
│       ├── JsonSharedOptions.cs      # 共享 JsonSerializerOptions
│       ├── AnalysisResult.cs         # 分析结果数据模型
│       ├── FieldStatistic.cs         # 字段统计数据模型
│       └── DuplicateObjectStatistic.cs # 重复对象数据模型
├── ObjectAnalyzer.sln                # 解决方案文件
└── README.md
```

## 技术栈

- **语言**：C# / .NET 10
- **文件解析**：[MiniExcel](https://github.com/mini-software/MiniExcel)（Excel / CSV）
- **命令行**：[System.CommandLine](https://github.com/dotnet/command-line-api)（beta4）
- **序列化**：System.Text.Json（内置）
- **XML 解析**：System.Xml.Linq（内置）
- **YAML 解析**：[YamlDotNet](https://github.com/aaubry/YamlDotNet)
- **运行时编译**：[Roslyn](https://github.com/dotnet/roslyn)（Microsoft.CodeAnalysis.CSharp）

## 许可证

MIT
