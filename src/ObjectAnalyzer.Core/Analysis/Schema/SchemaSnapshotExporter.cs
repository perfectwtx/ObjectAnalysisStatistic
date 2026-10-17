using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 将分析结果导出为 canonical Schema 快照 JSON（独立于代码生成器 SchemaExporter）。
/// 用途：作为 schema 演进的基线；与 <see cref="SchemaDiffer"/> 配合做版本对比。
/// </summary>
public static class SchemaSnapshotExporter
{
    /// <summary>当前导出器版本（与 SchemaSnapshot.SnapshotVersion 同步）。</summary>
    public const string CurrentVersion = "2.0";

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Save(AnalysisResult result, string outputPath, string? source = null)
    {
        var snapshot = Build(result, source);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(snapshot, s_writeOptions), new UTF8Encoding(false));
        AnalysisServices.Current.Console.MarkupLine(
            $"[green]Schema 快照已导出至[/] [blue]{MarkupText.Escape(outputPath)}[/] [grey]({snapshot.Fields.Count} 个字段，{snapshot.TotalObjects} 个对象)[/]");
    }

    /// <summary>
    /// 从 <see cref="AnalysisResult"/> 构造 <see cref="SchemaSnapshot"/>，字段顺序与
    /// <see cref="AnalysisResult.FieldStatistics"/> 一致（按源数据首次出现顺序）。
    /// </summary>
    public static SchemaSnapshot Build(AnalysisResult result, string? source = null)
    {
        var snapshot = new SchemaSnapshot
        {
            SnapshotVersion = CurrentVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = string.IsNullOrWhiteSpace(source) ? null : source,
            TotalObjects = result.TotalObjects
        };

        foreach (var field in result.FieldStatistics)
        {
            snapshot.Fields.Add(ConvertField(field));
        }

        return snapshot;
    }

    private static SchemaSnapshotField ConvertField(FieldStatistic field)
    {
        var snap = new SchemaSnapshotField
        {
            Path = field.FieldName,
            PrimaryType = string.IsNullOrEmpty(field.PrimaryType) ? "Unknown" : field.PrimaryType,
            SemanticType = field.SemanticType,
            Nullable = field.NullAppeared,
            Coverage = field.Coverage
        };

        // 类型分布：主导类型之外的次要类型
        if (field.TypeCounts != null && field.TypeCounts.Count > 0)
        {
            string primary = snap.PrimaryType;
            foreach (var (t, c) in field.TypeCounts)
            {
                if (string.Equals(t, primary, StringComparison.Ordinal))
                    continue;
                snap.TypeDistribution[t] = c;
            }
        }

        // 数值范围
        if (string.Equals(snap.PrimaryType, "Number", StringComparison.Ordinal))
        {
            snap.MinNumber = field.MinNumber;
            snap.MaxNumber = field.MaxNumber;
        }

        // 字符串长度
        if (field.MinLength.HasValue || field.MaxLength.HasValue)
        {
            snap.MinLength = field.MinLength;
            snap.MaxLength = field.MaxLength;
        }

        // 时间戳范围（统一为 UTC）
        if (field.MinDateTime.HasValue || field.MaxDateTime.HasValue)
        {
            snap.MinDateTime = field.MinDateTime?.ToUniversalTime();
            snap.MaxDateTime = field.MaxDateTime?.ToUniversalTime();
        }

        // 枚举候选值：来自规则文件
        if (field.AppliedRule?.EnumValues is { Count: > 0 } enumValues)
        {
            snap.EnumValues = enumValues
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            if (snap.EnumValues.Count == 0)
            {
                snap.EnumValues = null;
            }
        }

        // 异常值指标
        if (field.OutlierCount.HasValue)
        {
            snap.OutlierMethod = field.OutlierMethod;
            snap.OutlierRatio = field.OutlierRatio;
        }

        // 唯一值数量（精确或近似）
        if (field.DistinctCount.HasValue)
        {
            snap.DistinctCount = field.DistinctCount;
        }

        return snap;
    }
}
