# Desktop 使用与发布

ObjectAnalyzer Desktop 是基于 WPF 的 Windows 本地分析工作台，直接复用 ObjectAnalyzer.Core，不需要启动 Web 或 API。

## 本地运行

```powershell
dotnet restore ObjectAnalyzer.sln
dotnet run --project src/ObjectAnalyzer.Desktop
```

启动后可以：

- 选择 JSON、JSONL、CSV、Excel、XML、YAML 或压缩数据文件。
- 执行本地对象分析并查看概览、Schema、重复、深度分析和质量问题。
- 查看数据血缘、影响分析、Schema Impact 与回归结果。
- 查看质量基线和质量趋势。
- 查看本地作业历史。
- 查看和重新加载历史 JSON 报告。
- 导出字段 CSV。
- 长时间分析过程中可以取消当前作业。

## 本地数据位置

Desktop 使用当前用户的 Local Application Data 目录保存本地状态：

```text
%LOCALAPPDATA%/ObjectAnalyzer/
├── object-analyzer.db
└── Reports/
```

SQLite 数据库存放作业、质量快照、质量问题和质量基线。

JSON 报告保留为独立文件，便于复制、归档和重新加载。

## Windows 发布

推荐使用仓库中的 PowerShell 脚本：

```powershell
./scripts/publish-desktop.ps1
```

默认生成：

```text
artifacts/
├── desktop/
│   └── win-x64/
└── ObjectAnalyzer-Desktop-win-x64.zip
```

发布使用：

- Runtime：win-x64
- Configuration：Release
- Self-contained：是
- Single-file：是
- Native libraries：自动随单文件自解压

因此目标 Windows 机器不需要预先安装 .NET Runtime。

## GitHub Actions

.github/workflows/desktop.yml 会在 Desktop 或 Core 变化并推送到 main 时自动生成 Windows 发布 ZIP，也可以从 GitHub Actions 手动运行。

构建产物名称：

```text
ObjectAnalyzer-Desktop-win-x64
```

## 发布范围

当前发布方式是 ZIP 绿色版，不写入系统安装器，也不修改注册表。

后续需要正式安装体验时，可以在这个发布目录基础上增加 MSIX、WiX MSI 或其他 Windows 安装器，而不需要修改 Desktop 的分析逻辑。
