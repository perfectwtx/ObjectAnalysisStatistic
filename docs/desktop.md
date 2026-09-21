# Desktop 使用与发布

ObjectAnalyzer Desktop 是基于 WPF 的 Windows 本地分析工作台，直接复用 ObjectAnalyzer.Core，不需要启动 Web 或 API。

## 本地运行

```powershell
dotnet restore ObjectAnalyzer.sln
dotnet run --project src/ObjectAnalyzer.Desktop
```

启动后可以：

- 选择 JSON、JSONL、CSV、Excel、XML、YAML 或压缩数据文件。
- 将受支持的数据文件直接拖入主窗口的“分析文件”输入框。
- 从“最近文件”下拉列表快速载入最近使用的文件，最多保留 10 条本机记录。
- 执行本地对象分析并查看概览、Schema、重复、深度分析和质量问题。
- 查看数据血缘、影响分析、Schema Impact 与回归结果。
- 查看质量基线和质量趋势。
- 查看本地作业历史。
- 查看和重新加载历史 JSON 报告。
- 导出字段 CSV。
- 长时间分析过程中可以取消当前作业。

最近文件列表保存在：

```text
%LOCALAPPDATA%/ObjectAnalyzer/recent-files.json
```

最近文件属于辅助信息，仅保存文件路径，不影响分析数据库和报告。

## 本地数据位置

Desktop 使用当前用户的 Local Application Data 目录保存本地状态：

```text
%LOCALAPPDATA%/ObjectAnalyzer/
├── object-analyzer.db
├── recent-files.json
└── Reports/
```

SQLite 数据库存放作业、质量快照、质量问题和质量基线。

JSON 报告保留为独立文件，便于复制、归档和重新加载。

## Windows 发布

推荐使用仓库中的 PowerShell 脚本：

发布包中已经同时包含安装和卸载脚本。安装使用当前用户目录，不需要管理员权限。

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

## 安装与卸载

解压 Windows ZIP 后，在包目录执行：

```powershell
./install-desktop.ps1
```

安装到：

```text
%LOCALAPPDATA%/Programs/ObjectAnalyzer
```

同时创建开始菜单快捷方式。

卸载：

```powershell
./uninstall-desktop.ps1
```

当前安装流程只使用当前用户目录，不写入系统范围的 Program Files，也不要求管理员权限。

## 发布范围

当前发布方式是 ZIP + 用户级 PowerShell 安装脚本。它不依赖注册表安装机制。

后续需要企业部署或标准“下一步 / 安装”体验时，可以在现有发布目录基础上增加 MSIX、WiX MSI 或其他 Windows 安装器，而不需要修改 Desktop 的分析逻辑。

