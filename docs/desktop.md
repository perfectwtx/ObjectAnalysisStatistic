# Desktop 使用与发布

ObjectAnalyzer Desktop 是基于 WPF + [WPF UI](https://github.com/lepoco/wpfui) 的 Windows 本地分析工作台，直接复用 ObjectAnalyzer.Core，不需要启动 Web 或 API。

界面按专业工具布局组织：标题栏、菜单栏、工具栏、齐平导航、工作区、状态栏。色板为近黑底 + 钢蓝强调色，面板圆角 6px，避免消费级卡片堆叠。

## 本地运行

```powershell
dotnet restore ObjectAnalyzer.sln
dotnet run --project src/ObjectAnalyzer.Desktop
```

启动后可以：

- 选择 JSON、JSONL、CSV、Excel、XML、YAML 或压缩数据文件。
- 将受支持的数据文件直接拖入主窗口的“分析文件”输入框（路径单行完整显示，悬停可看完整路径）。
- 从“最近文件”下拉列表快速载入最近使用的文件，最多保留 10 条本机记录。
- 用菜单或工具栏执行打开文件、开始分析、取消、打开报告。快捷键：`Ctrl+O` 打开文件，`F5` 开始分析。
- 在左侧导航或「查看」菜单中切换分析 / 质量 / 历史页面。
- 执行本地对象分析并查看概览、Schema、重复、深度分析和质量问题。
- 查看数据血缘、影响分析、Schema Impact 与回归结果。
- 查看质量基线和质量趋势。
- 查看本地作业历史。
- 查看和重新加载历史 JSON 报告。
- 导出字段 CSV。
- 长时间分析过程中可以取消当前作业。
- 在工具栏右侧切换**语言（中文 / English）**与**主题（浅色 / 深色 / 跟随系统 / 护眼）**；工作区页仍可修改同一偏好。

最近文件列表保存在：

```text
%LOCALAPPDATA%/ObjectAnalyzer/recent-files.json
```

语言与主题偏好保存在：

```text
%LOCALAPPDATA%/ObjectAnalyzer/ui-settings.json
```

最近文件与 UI 偏好都属于辅助信息，不影响分析数据库和报告。

## 规则配置

在分析页点击「规则配置…」，可以导入 JSON/JSONC 规则文件，或从示例模板开始：

- **Runtime**：设置 `runtime.flatten`，其他运行参数保留在高级 JSONC 中。
- **Rules**：新增、删除或改名字段；编辑必填、唯一性、最小/最大值、空值率上限和正则。
- **Features**：设置最小行数（64 位非负整数）与最大重复率（0～1）。
- **高级 JSONC**：编辑完整规则，包括 `transform.parse`、`valueParsers`、枚举和 PII。

字段名称不能重复。字段改名时保留该行的高级属性；删除字段会移除整条规则。
切换表单与 JSONC 时会同步配置，解析失败会保留 JSONC 原文并显示错误，不会用旧表单覆盖。
直接编辑 JSONC 时保留注释和尾随逗号；修改可视化表单后会重新格式化为 JSON，注释不保留。

「校验规则」会使用 Core 模型检查结构与未知键，并检查范围顺序、比例、正则语法和行数。
「保存到…」保存文件并继续编辑；「保存并应用」保存后将路径回填分析页。
默认规则路径为 `%LOCALAPPDATA%/ObjectAnalyzer/rules/rules.jsonc`。
保存使用同目录临时文件再替换目标文件，失败时不截断原文件。
关闭、导入或载入模板前，如果有未保存的修改会提示确认。

Core 的统一规则读取支持 JSONC 注释和尾随逗号，编辑器保存的规则可直接用于本地分析。

## UI 技术栈

- **WPF UI (lepoco)**：`FluentWindow`、`TitleBar`、Fluent 按钮与控件样式。
- **工具壳层**：标准菜单 + 工具栏 + 齐平侧栏 + 底部状态栏（进度、当前文件）。
- **自有主题资源**：`Brush.*` 与护眼色板由 `Themes/*.xaml` 提供，深色/浅色强调色为钢蓝（`#8EB0FF` / `#2F5F9E`），并与 WPF UI 的 `ApplicationThemeManager` 同步。
- **本地化**：`Localization/Strings.*.xaml` + `LocalizationService`。

## 页签分组

结果区使用两级页签：

| 分组 | 子页签 |
|------|--------|
| **分析** | 概览、Schema、重复分析、深度分析、血缘 / 影响 / 回归、分析输出 |
| **质量** | 质量问题、质量基线与趋势 |
| **历史** | 作业中心、报告、工作区 |

无数据时，列表类页面会显示空状态说明，而不是空白表格。结果页使用齐平工具表：顶栏标题/过滤/操作，下方表格贴边，不再用圆角卡片堆叠。

## 语言

工具栏语言下拉框：

- **中文**（默认）
- **English**

切换后主界面标题、按钮、页签、列头与空状态会立即切换，并写入 `ui-settings.json`。

## 主题

工具栏主题下拉框支持四种模式：

| 模式 | 说明 |
|------|------|
| **浅色** | WPF UI Light + 自有浅色钢蓝色板 |
| **深色** | WPF UI Dark + 自有近黑 / 钢蓝色板 |
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
