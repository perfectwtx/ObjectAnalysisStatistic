using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ObjectAnalyzer.Analysis;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

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
            var mask = PiiDetector.TryGetMaskFormat(stat.SemanticType, stat.AppliedRule);
            if (mask == null)
                continue;

            // 按出现次数倒序取样本；次数相同时按字典序升序，保证多线程合并后的非确定性顺序下也能稳定复现
            string? sample = stat.ValueCounts
                .Where(kv => kv.Key != "[Other values]")
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .FirstOrDefault();

            string categoryKey = mask.Category.ToString().ToLowerInvariant();
            categoryCounts[categoryKey] = categoryCounts.GetValueOrDefault(categoryKey) + 1;
            piiValueCount += stat.Count;

            fields.Add(new PiiFieldReport
            {
                Field = stat.FieldName,
                Category = categoryKey,
                CategoryLabel = mask.Label,
                HighSeverity = mask.HighSeverity,
                Count = stat.Count,
                Coverage = stat.Coverage,
                DistinctValues = stat.ValueCounts.Keys.Count(k => k != "[Other values]"),
                MaskedSample = sample != null ? mask.Apply(sample) : null
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
            : "未在字段语义中检测到手机号、身份证号、银行卡号、姓名、邮箱、SSN、IBAN 或护照号。";

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
            AnsiConsole.MarkupLine("[green]数据安全合规: 未发现敏感字段（手机号/身份证号/银行卡号/姓名/邮箱/SSN/IBAN/护照号）。[/]");
            return;
        }

        string categories = string.Join("/", report.Fields
            .Select(f => f.CategoryLabel)
            .Distinct());
        AnsiConsole.MarkupLine(
            $"[yellow]数据安全合规: 发现 [bold]{report.PiiFieldCount}[/] 个敏感字段（{Markup.Escape(categories)}），"
            + $"共约 {report.PiiValueCount} 个敏感值，报告输出将自动打码。"
            + "推出合规等级报告请用 --pii-report。[/]");
    }
}
