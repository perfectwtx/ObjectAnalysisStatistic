# Desktop 使用与发布

ObjectAnalyzer Desktop 是基于 WPF + [WPF UI](https://github.com/lepoco/wpfui) 的 Windows 本地分析工作台，直接复用 ObjectAnalyzer.Core，不需要启动 Web 或 API。

## 本地运行

```powershell
dotnet restore ObjectAnalyzer.sln
dotnet run --project src/ObjectAnalyzer.Desktop
```

启动后可以：

- 选择 JSON、JSONL、CSV、Excel、XML、YAML 或压缩数据文件。
- 将受支持的数据文件直接拖入主窗口的“分析文件”输入框（路径单行完整显示，悬停可看完整路径）。
- 从“最近文件”下拉列表快速载入最近使用的文件，最多保留 10 条本机记录。
- 在**分析 / 质量 / 历史**三组页签中查看结果。
- 执行本地对象分析并查看概览、Schema、重复、深度分析和质量问题。
- 查看数据血缘、影响分析、Schema Impact 与回归结果。
- 查看质量基线和质量趋势。
- 查看本地作业历史。
- 查看和重新加载历史 JSON 报告。
- 导出字段 CSV。
- 长时间分析过程中可以取消当前作业。
- 在窗口右上角切换**语言（中文 / English）**与**主题（浅色 / 深色 / 跟随系统 / 护眼）**。

最近文件列表保存在：

```text
%LOCALAPPDATA%/ObjectAnalyzer/recent-files.json
```

语言与主题偏好保存在：

```text
%LOCALAPPDATA%/ObjectAnalyzer/ui-settings.json
```

最近文件与 UI 偏好都属于辅助信息，不影响分析数据库和报告。

## UI 技术栈

- **WPF UI (lepoco)**：`FluentWindow`、`TitleBar`、`Card`、Fluent 按钮与控件样式，支持 Mica 背景。
- **自有主题资源**：`Brush.*` 与护眼色板仍由 `Themes/*.xaml` 提供，并与 WPF UI 的 `ApplicationThemeManager` 同步。
- **本地化**：`Localization/Strings.*.xaml` + `LocalizationService`。

## 页签分组

结果区使用两级页签：

| 分组 | 子页签 |
|------|--------|
| **分析** | 概览、Schema、重复分析、深度分析、血缘 / 影响 / 回归、分析输出 |
| **质量** | 质量问题、质量基线与趋势 |
| **历史** | 作业中心、报告、工作区 |

无数据时，列表类页面会显示空状态说明，而不是空白表格。

## 语言

主窗口右上角语言下拉框：

- **中文**（默认）
- **English**

切换后主界面标题、按钮、页签等会立即切换，并写入 `ui-settings.json`。

> 说明：部分子页面内的固定文案仍可能以中文为主，后续可继续补全字符串资源。

## 主题

主窗口右上角主题下拉框支持四种模式：

| 模式 | 说明 |
|------|------|
| **浅色** | WPF UI Light + 自有浅色色板 |
| **深色** | WPF UI Dark + 自有深色色板 |
| **跟随系统** | 跟随 Windows 应用浅色/深色，系统变更时自动同步 |
| **护眼** | WPF UI Light 窗体 + 柔和绿纸色板 |

偏好会持久化到 `ui-settings.json`，下次启动自动恢复。

## 本地数据位置

Desktop 使用当前用户的 Local Application Data 目录保存本地状态：

```text
%LOCALAPPDATA%/ObjectAnalyzer/
├── object-analyzer.db
├── recent-files.json
├── ui-settings.json
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
