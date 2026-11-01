using System.Globalization;
using System.IO;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed class DesktopFieldRow
{
    public string FieldName { get; }
    public string PrimaryType { get; }
    public string CoverageText { get; }
    public double CoverageRatio { get; }
    public long Count { get; }
    public long NullCount { get; }
    public long NonNullCount => Math.Max(0, Count - NullCount);
    public long? DistinctCount { get; }
    public string DistinctDisplayText => DistinctCount is not long distinct
        ? "—"
        : DistinctApproximate
            ? $"≈ {distinct:N0}"
            : distinct.ToString("N0", CultureInfo.InvariantCulture);
    public bool DistinctApproximate { get; }
    public bool ValueCountsTruncated { get; }
    public string? SemanticType { get; }
    public string SemanticTypeDisplay =>
        SemanticType ?? LocalizationService.Get("Str.Schema.Unrecognized", "未识别");
    public string? NumericRangeText { get; }
    public string NumericRangeDisplay => NumericRangeText ?? "—";
    public string NumericRangeTooltip =>
        NumericRangeText ?? LocalizationService.Get("Str.Schema.NotApplicable", "不适用");
    public string? QuantilesText { get; }
    public IReadOnlyList<DesktopFieldValueRow> Values { get; }
    public IReadOnlyList<DesktopFieldTypeRow> Types { get; }

    public DesktopFieldRow(FieldStatistic source, long totalObjects = 0)
    {
        FieldName = source.FieldName;
        PrimaryType = source.PrimaryType;
        CoverageText = source.Coverage.ToString("P1", CultureInfo.InvariantCulture);
        CoverageRatio = Math.Clamp(source.Coverage, 0d, 1d);
        Count = source.Count;
        NullCount = source.NullCount;
        DistinctCount = source.DistinctCount;
        DistinctApproximate = source.DistinctApproximate;
        ValueCountsTruncated = source.ValueCountsTruncated;
        SemanticType = source.SemanticType;
        NumericRangeText = source.MinNumber is not null && source.MaxNumber is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{source.MinNumber:G6} ~ {source.MaxNumber:G6}")
            : null;
        QuantilesText = source.FormatQuantiles();

        long denom = Count > 0 ? Count : 1;
        Values = source.ValueCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key.CanonicalKey, StringComparer.Ordinal)
            .Select(x => new DesktopFieldValueRow(
                x.Key.ToDisplayString(),
                x.Value,
                (double)x.Value / denom))
            .ToList();

        Types = source.TypeCounts
            .OrderByDescending(x => x.Value)
            .Select(x => new DesktopFieldTypeRow(x.Key, x.Value))
            .ToList();
    }
}

public sealed class DesktopFieldValueRow
{
    public string Value { get; }
    public long Count { get; }
    public string ShareText { get; }

    public DesktopFieldValueRow(string value, long count, double share)
    {
        Value = string.IsNullOrEmpty(value) ? "(空字符串)" : value;
        Count = count;
        ShareText = share.ToString("P2", CultureInfo.InvariantCulture);
    }
}

public sealed class DesktopFieldTypeRow
{
    public string TypeName { get; }
    public long Count { get; }

    public DesktopFieldTypeRow(string typeName, long count)
    {
        TypeName = typeName;
        Count = count;
    }
}

public sealed class DesktopDuplicateRow
{
    public string Key { get; }
    public string FullJson { get; }
    public long Count { get; }

    public DesktopDuplicateRow(DuplicateObjectStatistic source)
    {
        FullJson = source.ObjectJson;
        string summary = string.Join(" ", source.ObjectJson.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Key = summary.Length > 120 ? summary[..120] + "…" : summary;
        Count = source.Count;
    }
}

public sealed class DesktopIssueRow
{
    public string FieldName { get; }
    public string Check { get; }
    public long Count { get; }
    public string Message { get; }

    public DesktopIssueRow(FieldViolation source)
    {
        FieldName = source.Field;
        Check = source.Check;
        Count = source.Count;
        Message = source.Message;
    }
}

public sealed class DesktopReportRow
{
    public string Path { get; }
    public string Name { get; }
    public string TimeText { get; }
    public string SizeText { get; }

    public DesktopReportRow(string path)
    {
        Path = path;
        var file = new FileInfo(path);
        Name = file.Name;
        TimeText = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        SizeText = file.Length < 1024 * 1024
            ? $"{(file.Length / 1024d).ToString("N1", CultureInfo.InvariantCulture)} KB"
            : $"{(file.Length / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture)} MB";
    }
}

public sealed class DesktopQualitySnapshotRow
{
    public string SourceName { get; }
    public string TimeText { get; }
    public string OverallText { get; }
    public string CompletenessText { get; }
    public string ValidityText { get; }
    public string UniquenessText { get; }
    public string ConsistencyText { get; }
    public string AnomalyControlText { get; }
    public long TotalObjects { get; }
    public int IssueCount { get; }

    public DesktopQualitySnapshotRow(QualitySnapshot snapshot)
    {
        SourceName = snapshot.SourceName;
        TimeText = snapshot.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        OverallText = snapshot.Metrics.OverallScore.ToString("F2", CultureInfo.InvariantCulture);
        CompletenessText = snapshot.Metrics.Completeness.ToString("F2", CultureInfo.InvariantCulture);
        ValidityText = snapshot.Metrics.Validity.ToString("F2", CultureInfo.InvariantCulture);
        UniquenessText = snapshot.Metrics.Uniqueness.ToString("F2", CultureInfo.InvariantCulture);
        ConsistencyText = snapshot.Metrics.Consistency.ToString("F2", CultureInfo.InvariantCulture);
        AnomalyControlText = snapshot.Metrics.AnomalyControl.ToString("F2", CultureInfo.InvariantCulture);
        TotalObjects = snapshot.TotalObjects;
        IssueCount = snapshot.IssueCount;
    }
}

public sealed class DesktopJobRow
{
    public Guid Id { get; }
    public string SourceName { get; }
    public string SourceType { get; }
    public string StatusText { get; }
    public string StartedText { get; }
    public string DurationText { get; }
    public long TotalObjects { get; }
    public int FieldCount { get; }
    public string QualityScoreText { get; }
    public int IssueCount { get; }
    public string? ErrorMessage { get; }

    public DesktopJobRow(AnalysisJob job)
    {
        Id = job.Id;
        SourceName = job.SourceName;
        SourceType = job.SourceType;
        StatusText = job.Status switch
        {
            AnalysisJobStatus.Pending => "等待",
            AnalysisJobStatus.Running => "运行中",
            AnalysisJobStatus.Succeeded => "成功",
            AnalysisJobStatus.Failed => "失败",
            AnalysisJobStatus.Cancelled => "已取消",
            _ => job.Status.ToString()
        };
        StartedText = job.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        DurationText = job.DurationMs > 0
            ? $"{job.DurationMs.ToString("N0", CultureInfo.InvariantCulture)} ms"
            : "-";
        TotalObjects = job.TotalObjects;
        FieldCount = job.FieldCount;
        QualityScoreText = job.QualityScore?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
        IssueCount = job.IssueCount;
        ErrorMessage = job.ErrorMessage;
    }
}
