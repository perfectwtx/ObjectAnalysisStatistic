using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分布快照导出器：从 <see cref="AnalysisResult"/> 提取 t-digest / HLL / 摘要，输出 JSON。
/// </summary>
public static class DistributionSnapshotExporter
{
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_loadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>从分析结果与流式度量构建分布快照。</summary>
    public static DistributionSnapshot Build(
        AnalysisResult result,
        Dictionary<string, MergeableQuantileSketch>? quantileSketches,
        Dictionary<string, HyperLogLog>? distinctSketches,
        string? source = null)
    {
        var snap = new DistributionSnapshot
        {
            Source = source,
            TotalObjects = result.TotalObjects,
            CreatedAt = DateTimeOffset.UtcNow
        };
        foreach (var stat in result.FieldStatistics)
        {
            var field = new DistributionSnapshotField
            {
                Path = stat.FieldName,
                PrimaryType = stat.PrimaryType,
                Coverage = stat.Coverage,
                Count = stat.Count
            };
            if (stat.PrimaryType == "Number")
            {
                field.MeanNumber = stat.AvgNumber;
                field.StdDevNumber = stat.StdDevNumber;
                field.MinNumber = stat.MinNumber;
                field.MaxNumber = stat.MaxNumber;
                if (quantileSketches != null && quantileSketches.TryGetValue(stat.FieldName, out var q) && q.TotalWeight > 0)
                {
                    field.QuantileCentroids = QuantileCentroidExtractor.Extract(q);
                }
            }
            if (stat.PrimaryType == "String" || stat.PrimaryType == "DateTime")
            {
                field.MinLength = stat.MinLength;
                field.MaxLength = stat.MaxLength;
                field.AvgLength = stat.AvgLength;
            }
            if (distinctSketches != null && distinctSketches.TryGetValue(stat.FieldName, out var hll))
            {
                field.DistinctHllRegisters = HllSerializer.ToBase64(hll);
                field.DistinctHllPrecision = HllSerializer.GetPrecision(hll);
                field.DistinctCount = hll.Estimate();
            }
            else if (stat.DistinctCount.HasValue)
            {
                field.DistinctCount = stat.DistinctCount;
            }
            snap.Fields.Add(field);
        }
        return snap;
    }

    public static void WriteToFile(DistributionSnapshot snapshot, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, s_writeOptions), new UTF8Encoding(false));
    }

    public static DistributionSnapshot LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"分布快照文件不存在: {path}", path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        var snap = JsonSerializer.Deserialize<DistributionSnapshot>(json, s_loadOptions);
        if (snap == null) throw new InvalidDataException($"分布快照解析失败: {path}");
        return snap;
    }
}
