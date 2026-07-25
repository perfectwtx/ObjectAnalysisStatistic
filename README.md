# ObjectAnalyzer

对象字段分析统计工具 — 支持 JSON / JSONL / CSV / Excel 输入，自动统计字段频次、数据类型、值分布及重复对象。

## 功能特性

- **多格式支持**：JSON（数组/单对象）、JSONL（NDJSON）、CSV、Excel（.xlsx）
- **字段统计**：每个字段的出现频次、覆盖率、数据类型分布
- **值频率分析**：展示每个字段中唯一值的出现次数与占比
- **默认值推断**：自动推断字段可能的默认值（`null` / `0` / `false`）
- **重复对象检测**：识别并统计完全相同的重复对象
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
| `--input <FILE>` | `-i` | 输入文件路径（JSON / JSONL / CSV / Excel） | 必填 |
| `--output <FILE>` | `-o` | 输出报告文件路径（.json） | 无 |
| `--format <FORMAT>` | `-f` | 显式指定输入文件格式：`json`、`jsonl`、`csv`、`excel` | 自动检测 |
| `--no-flatten` | — | 解析 JSON 时不扁平化嵌套属性路径 | `false` |
| `--max-values <N>` | `-m` | 控制台报告中每个字段最多展示的唯一值数量 | `5` |
| `--generate-test-excel` | — | 生成 Excel 测试文件 `samples/test_data.xlsx` 并退出 | — |

### 示例

**分析 JSON 文件并在控制台显示结果：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

**分析 CSV 文件并导出 JSON 报告：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.csv -o report.json
```

**分析 JSONL 文件，不扁平化嵌套结构，最多展示 10 个唯一值：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.jsonl --no-flatten -m 10
```

**显式指定格式分析 Excel 文件：**

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.xlsx -f excel
```

## 项目结构

```
ObjectAnalysisStatistic/
├── samples/                          # 示例数据与报告
│   ├── test_data.json
│   ├── test_data.jsonl
│   ├── test_data.csv
│   └── test_data.xlsx
├── src/
│   └── ObjectAnalyzer/                 # 主项目
│       ├── Program.cs                  # 入口 & 命令行参数定义
│       ├── AnalysisRunner.cs           # 分析流程编排
│       ├── ObjectAnalyzer.cs           # 核心分析逻辑
│       ├── FileParsers.cs              # 多格式文件解析器
│       ├── ConsoleReporter.cs          # 控制台报告输出
│       ├── ReportExporter.cs           # JSON 报告导出
│       ├── AnalysisResult.cs           # 分析结果数据模型
│       ├── FieldStatistic.cs           # 字段统计数据模型
│       └── DuplicateObjectStatistic.cs # 重复对象数据模型
├── ObjectAnalyzer.sln                # 解决方案文件
└── README.md
```

## 技术栈

- **语言**：C# / .NET 10
- **文件解析**：[MiniExcel](https://github.com/mini-software/MiniExcel)（Excel / CSV）
- **命令行**：[System.CommandLine](https://github.com/dotnet/command-line-api)（beta4）
- **序列化**：System.Text.Json（内置）

## 许可证

MIT
