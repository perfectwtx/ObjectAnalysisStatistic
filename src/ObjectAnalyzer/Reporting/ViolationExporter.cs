using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将枚举违规导出为独立的结构化 JSON 文件，便于 CI 断言/程序消费。
/// 输出格式: [{ "field": "Gender", "values": ["xxx", "yyy"] }, ...]
/// </summary>
public static class ViolationExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        result = PiiRedactor.Redact(result);

        var violations = result.FieldStatistics
            .Where(f => f.EnumViolations.Count > 0)
            .Select(f => new
            {
                field = f.FieldName,
                values = (IReadOnlyList<string>)f.EnumViolations
            })
            .ToList();

        string json = JsonSerializer.Serialize(violations, JsonSharedOptions.Write);
        File.WriteAllText(outputPath, json);

        AnsiConsole.MarkupLine($"已导出 [yellow]{violations.Count}[/] 个字段的枚举违规至: [blue]{Markup.Escape(outputPath)}[/]");
    }
}
