using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ObjectAnalyzer.Analysis;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>合规报告中单个敏感字段的明细。</summary>
public sealed class PiiFieldReport
{
    public string Field { get; init; } = "";
    public string Category { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public bool HighSeverity { get; init; }
    public long Count { get; init; }
    public double Coverage { get; init; }
    public int DistinctValues { get; init; }
    public string? MaskedSample { get; init; }
}

/// <summary>
/// 数据安全合规等级报告：汇总分析结果中的个人敏感信息 (PII) 字段，
/// 给出合规等级（A/B/C）、敏感字段清单（已打码样本）与各类别统计。
/// 用于人工快速判断共享该报告/代码定义前的合规风险。
/// </summary>
public sealed class PiiReport
{
    public string GeneratedAt { get; init; } = "";
    public string ComplianceLevel { get; init; } = "A";
    public string ComplianceLevelLabel { get; init; } = "";
    public bool MaskingApplied { get; init; }
    public string Summary { get; init; } = "";
    public int TotalFields { get; init; }
    public int PiiFieldCount { get; init; }
    public long PiiValueCount { get; init; }
    public List<PiiFieldReport> Fields { get; init; } = new();
    public Dictionary<string, int> CategoryCounts { get; init; } = new();

    public static PiiReport Build(AnalysisResult result)
    {
        var fields = new List<PiiFieldReport>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        long piiValueCount = 0;

        foreach (var stat in result.FieldStatistics)
        {
            var category = PiiDetector.FromSemanticType(stat.SemanticType);
            if (category == PiiCategory.None)
                continue;

            string? sample = stat.ValueCounts
                .OrderByDescending(kv => kv.Value)
                .Where(kv => kv.Key != "[Other values]")
                .Select(kv => kv.Key)
                .FirstOrDefault();

            categoryCounts[category.ToString().ToLowerInvariant()] =
                categoryCounts.GetValueOrDefault(category.ToString().ToLowerInvariant()) + 1;
            piiValueCount += stat.Count;

            fields.Add(new PiiFieldReport
            {
                Field = stat.FieldName,
                Category = category.ToString().ToLowerInvariant(),
                CategoryLabel = PiiDetector.CategoryLabel(category),
                HighSeverity = PiiDetector.IsHighSeverity(category),
                Count = stat.Count,
                Coverage = stat.Coverage,
                DistinctValues = stat.ValueCounts.Keys.Count(k => k != "[Other values]"),
                MaskedSample = sample != null ? PiiDetector.Mask(sample, category) : null
            });
        }

        fields = fields
            .OrderByDescending(f => f.HighSeverity)
            .ThenByDescending(f => f.Count)
            .ThenBy(f => f.Field, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool any = fields.Count > 0;
        bool allSamplesMasked = fields.All(f => f.MaskedSample != null);

        string level = any ? (allSamplesMasked ? "B" : "C") : "A";
        string label = any
            ? (allSamplesMasked
                ? "存在敏感字段，报告输出已全部自动打码，可安全共享"
                : "存在敏感字段且样本无法完全打码，需人工复核")
            : "未发现敏感字段";

        string summary = any
            ? $"发现 {fields.Count} 个敏感字段 / 共 {piiValueCount} 个敏感值："
              + string.Join("、", fields.Select(f => $"{f.CategoryLabel}({f.Field})"))
            : "未在字段语义中检测到手机号、身份证号、银行卡号、姓名或邮箱。";

        return new PiiReport
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ComplianceLevel = level,
            ComplianceLevelLabel = label,
            MaskingApplied = any,
            Summary = summary,
            TotalFields = result.FieldStatistics.Count,
            PiiFieldCount = fields.Count,
            PiiValueCount = piiValueCount,
            Fields = fields,
            CategoryCounts = categoryCounts
        };
    }

    /// <summary>向控制台输出一行数据安全合规摘要（无论是否导出报告文件）。</summary>
    public static void PrintSummary(AnalysisResult result)
    {
        var report = Build(result);

        if (report.PiiFieldCount == 0)
        {
            AnsiConsole.MarkupLine("[green]数据安全合规: 未发现敏感字段（手机号/身份证号/银行卡号/姓名/邮箱）。[/]");
            return;
        }

        string categories = string.Join("/", report.CategoryCounts
            .OrderBy(kv => kv.Key)
            .Select(kv => PiiDetector.CategoryLabel(ParseCategory(kv.Key))));
        AnsiConsole.MarkupLine(
            $"[yellow]数据安全合规: 发现 [bold]{report.PiiFieldCount}[/] 个敏感字段（{Markup.Escape(categories)}），"
            + $"共约 {report.PiiValueCount} 个敏感值，报告输出将自动打码。"
            + "推出合规等级报告请用 --pii-report。[/]");
    }

    private static PiiCategory ParseCategory(string key)
    {
        foreach (PiiCategory category in Enum.GetValues<PiiCategory>())
        {
            if (string.Equals(category.ToString(), key, StringComparison.OrdinalIgnoreCase))
                return category;
        }
        return PiiCategory.None;
    }
}