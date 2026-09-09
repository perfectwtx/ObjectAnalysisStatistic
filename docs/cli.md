# CLI

CLI 项目位于 `src/ObjectAnalyzer`，命令名称为 `objectanalyzer`。

## 源码运行

```bash
dotnet run --project src/ObjectAnalyzer -- -i samples/test_data.json
```

## 常用参数

```text
-i, --input                 输入数据
-o, --output                输出报告
-r, --rules                 规则文件
-m, --max-values            控制台展示的值数量
--max-tracked-values        分析阶段跟踪的唯一值上限
--where                     数据过滤表达式
```

完整参数以当前 CLI 帮助输出和 `docs/configuration.md` 为准。

## 输出

报告根据输出文件扩展名选择格式，可生成 JSON、HTML、Markdown、CSV、Excel 或 PDF 等结果。

没有指定输出文件时，可以直接查看控制台结果。

## 质量门禁

CLI 可以将质量检查、合规检查、异常值检查和 Schema 演进检查作为门禁使用。门禁失败通过退出码反馈给 CI/CD。

## 自动化

CLI 适合用于：

- CI 数据质量检查
- 数据文件发布前检查
- Schema 变更验证
- 批处理分析
- 本地数据探查
