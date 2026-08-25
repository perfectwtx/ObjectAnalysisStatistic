namespace ObjectAnalyzer.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ObjectAnalyzer.Analysis;

/// <summary>
/// Schema 快照 + Diff 单元测试：覆盖 SchemaSnapshotExporter / SchemaDiffer / 破坏性变更标记。
/// </summary>
public class SchemaSnapshotTests
{
    // ──────────────────────────────────────────────
    // SchemaSnapshotExporter
    // ──────────────────────────────────────────────

    [Fact]
    public void SnapshotExporter_PreservesFieldOrder()
    {
        var result = BuildResult(new[]
        {
            MakeField("id", "Number", coverage: 1.0),
            MakeField("name", "String", semantic: "Name", coverage: 1.0),
            MakeField("createdAt", "String", semantic: "DateTime", coverage: 1.0,
                minDateTime: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                maxDateTime: new DateTimeOffset(2025, 1, 31, 0, 0, 0, TimeSpan.Zero))
        });

        var snapshot = SchemaSnapshotExporter.Build(result, "src.json");

        Assert.Equal(3, snapshot.Fields.Count);
        Assert.Equal("id", snapshot.Fields[0].Path);
        Assert.Equal("name", snapshot.Fields[1].Path);
        Assert.Equal("createdAt", snapshot.Fields[2].Path);
        Assert.Equal("1.0", snapshot.SnapshotVersion);
        Assert.Equal("src.json", snapshot.Source);
    }

    private static readonly string[] s_expectedEnums = new[] { "active", "inactive" };

    [Fact]
    public void SnapshotExporter_ConvertsEnumJsonElementsToStrings()
    {
        var enumElements = new List<System.Text.Json.JsonElement>
        {
            System.Text.Json.JsonDocument.Parse("\"active\"").RootElement.Clone(),
            System.Text.Json.JsonDocument.Parse("\"inactive\"").RootElement.Clone()
        };
        var stat = new FieldStatistic
        {
            FieldName = "status",
            PrimaryType = "String",
            Coverage = 1.0,
            Count = 3,
            AppliedRule = new FieldRule { EnumValues = enumElements }
        };
        var result = new AnalysisResult
        {
            TotalObjects = 3,
            FieldStatistics = new List<FieldStatistic> { stat }
        };

        var snapshot = SchemaSnapshotExporter.Build(result);

        Assert.NotNull(snapshot.Fields[0].EnumValues);
        Assert.Equal(s_expectedEnums, snapshot.Fields[0].EnumValues);
    }

    [Fact]
    public void SnapshotExporter_OmitsNullSecondaryTypeDistribution()
    {
        var stat = MakeField("id", "Number", coverage: 1.0);
        var result = new AnalysisResult
        {
            TotalObjects = 1,
            FieldStatistics = new List<FieldStatistic> { stat }
        };

        var snapshot = SchemaSnapshotExporter.Build(result);

        // Number 单类型时 typeDistribution 为空字典，序列化时该字段会被 JsonIgnore 略过
        Assert.Empty(snapshot.Fields[0].TypeDistribution);
    }

    [Fact]
    public void SnapshotExporter_ConvertsDatesToUtc()
    {
        DateTimeOffset local = new(2025, 6, 15, 10, 0, 0, TimeSpan.FromHours(8)); // +08:00
        var stat = MakeField("ts", "String", semantic: "DateTime", coverage: 1.0,
            minDateTime: local, maxDateTime: local);
        var result = new AnalysisResult
        {
            TotalObjects = 1,
            FieldStatistics = new List<FieldStatistic> { stat }
        };

        var snapshot = SchemaSnapshotExporter.Build(result);

        // 2025-06-15T10:00:00+08:00 == 2025-06-15T02:00:00Z
        Assert.Equal(new DateTimeOffset(2025, 6, 15, 2, 0, 0, TimeSpan.Zero),
            snapshot.Fields[0].MinDateTime);
    }

    [Fact]
    public void SnapshotExporter_SaveAndLoad_RoundTripsCleanly()
    {
        var stat = MakeField("score", "Number", coverage: 1.0,
            minNumber: 10, maxNumber: 90);
        stat.OutlierMethod = "iqr";
        stat.OutlierRatio = 0.05;
        stat.OutlierCount = 2; // 触发异常值信息序列化
        var result = new AnalysisResult
        {
            TotalObjects = 10,
            FieldStatistics = new List<FieldStatistic> { stat }
        };

        string path = Path.Combine(Path.GetTempPath(),
            $"schema-snap-test-{Guid.NewGuid():N}.json");
        try
        {
            SchemaSnapshotExporter.Save(result, path, "test.json");
            Assert.True(File.Exists(path));

            var loaded = SchemaDiffer.LoadFromFile(path);
            Assert.Single(loaded.Fields);
            Assert.Equal("score", loaded.Fields[0].Path);
            Assert.Equal(10.0, loaded.Fields[0].MinNumber);
            Assert.Equal(90.0, loaded.Fields[0].MaxNumber);
            Assert.Equal("iqr", loaded.Fields[0].OutlierMethod);
            Assert.Equal(0.05, loaded.Fields[0].OutlierRatio);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ──────────────────────────────────────────────
    // SchemaDiffer.Compute
    // ──────────────────────────────────────────────

    [Fact]
    public void Differ_AddedRemovedChanged_Categorized()
    {
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 5,
            Fields = new()
            {
                MakeSnap("id", "Number", coverage: 1.0, minNumber: 1, maxNumber: 5),
                MakeSnap("old_field", "String", coverage: 1.0),
                MakeSnap("name", "String", coverage: 1.0)
            }
        };
        var current = new SchemaSnapshot
        {
            TotalObjects = 7,
            Fields = new()
            {
                MakeSnap("id", "Number", coverage: 1.0, minNumber: 1, maxNumber: 5),  // 无变化
                MakeSnap("name", "String", coverage: 1.0),                              // 无变化
                MakeSnap("country", "String", coverage: 0.8),                            // 新增
            }
        };

        var diff = SchemaDiffer.Compute(baseline, current);

        Assert.Single(diff.AddedFields);
        Assert.Equal("country", diff.AddedFields[0].Path);
        Assert.Single(diff.RemovedFields);
        Assert.Equal("old_field", diff.RemovedFields[0].Path);
        Assert.Empty(diff.ChangedFields);
        Assert.True(diff.HasBreakingChanges, "字段被删除应是破坏性变更");
    }

    [Fact]
    public void Differ_TypeChange_MarkedBreaking()
    {
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 3,
            Fields = new() { MakeSnap("age", "Number", coverage: 1.0) }
        };
        var current = new SchemaSnapshot
        {
            TotalObjects = 3,
            Fields = new() { MakeSnap("age", "String", coverage: 1.0) }
        };

        var diff = SchemaDiffer.Compute(baseline, current);

        Assert.Single(diff.ChangedFields);
        var change = diff.ChangedFields[0];
        Assert.True(change.Breaking);
        Assert.Contains(change.Changes, c => c.Property == "primaryType" && c.Breaking);
        Assert.True(diff.HasBreakingChanges);
    }

    [Fact]
    public void Differ_NullableTightening_Breaking_NullableRelaxation_NotBreaking()
    {
        // nullable → non-null（破坏性）
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 3,
            Fields = new() { MakeSnap("email", "String", coverage: 1.0, nullable: true) }
        };
        var current = new SchemaSnapshot
        {
            TotalObjects = 3,
            Fields = new() { MakeSnap("email", "String", coverage: 1.0, nullable: false) }
        };
        var diff = SchemaDiffer.Compute(baseline, current);
        Assert.True(diff.ChangedFields[0].Breaking);
        Assert.True(diff.HasBreakingChanges);

        // non-null → nullable（非破坏性）
        baseline.Fields[0].Nullable = false;
        current.Fields[0].Nullable = true;
        diff = SchemaDiffer.Compute(baseline, current);
        Assert.False(diff.ChangedFields[0].Breaking);
        Assert.False(diff.HasBreakingChanges);
    }

    [Fact]
    public void Differ_CoverageDrop_Breaking_WhenOverThreshold()
    {
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 10,
            Fields = new() { MakeSnap("optional", "String", coverage: 1.0) }
        };
        // coverage 从 1.0 跌到 0.85，Δ=0.15 >= 0.10 阈值
        var current = new SchemaSnapshot
        {
            TotalObjects = 10,
            Fields = new() { MakeSnap("optional", "String", coverage: 0.85) }
        };

        var diff = SchemaDiffer.Compute(baseline, current);

        Assert.Single(diff.ChangedFields);
        Assert.True(diff.ChangedFields[0].Breaking);
        Assert.True(diff.HasBreakingChanges);
    }

    [Fact]
    public void Differ_CoverageDrop_NotBreaking_WhenBelowThreshold()
    {
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 100,
            Fields = new() { MakeSnap("optional", "String", coverage: 0.95) }
        };
        // coverage 从 0.95 跌到 0.92，Δ=0.03 < 0.10 阈值
        var current = new SchemaSnapshot
        {
            TotalObjects = 100,
            Fields = new() { MakeSnap("optional", "String", coverage: 0.92) }
        };

        var diff = SchemaDiffer.Compute(baseline, current);

        // coverage 微小变化应作为信息性变化，不标记破坏性
        var coverageChange = diff.ChangedFields[0].Changes.FirstOrDefault(c => c.Property == "coverage");
        Assert.NotNull(coverageChange);
        Assert.False(coverageChange.Breaking);
    }

    [Fact]
    public void Differ_NumberRangeChange_InformationalOnly()
    {
        var baseline = new SchemaSnapshot
        {
            TotalObjects = 5,
            Fields = new() { MakeSnap("score", "Number", coverage: 1.0, minNumber: 0, maxNumber: 100) }
        };
        var current = new SchemaSnapshot
        {
            TotalObjects = 5,
            Fields = new() { MakeSnap("score", "Number", coverage: 1.0, minNumber: 5, maxNumber: 95) }
        };

        var diff = SchemaDiffer.Compute(baseline, current);

        Assert.Single(diff.ChangedFields);
        Assert.False(diff.ChangedFields[0].Breaking);
        Assert.False(diff.HasBreakingChanges);
        var rangeChanges = diff.ChangedFields[0].Changes
            .Where(c => c.Property is "minNumber" or "maxNumber").ToList();
        Assert.Equal(2, rangeChanges.Count);
    }

    [Fact]
    public void Differ_NoChanges_ReturnsEmptyDiff()
    {
        var snap = new SchemaSnapshot
        {
            TotalObjects = 3,
            Fields = new()
            {
                MakeSnap("id", "Number", coverage: 1.0, minNumber: 1, maxNumber: 3),
                MakeSnap("name", "String", coverage: 1.0)
            }
        };
        var diff = SchemaDiffer.Compute(snap, snap);

        Assert.Empty(diff.AddedFields);
        Assert.Empty(diff.RemovedFields);
        Assert.Empty(diff.ChangedFields);
        Assert.False(diff.HasBreakingChanges);
    }

    [Fact]
    public void Differ_LoadFromFile_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(
            () => SchemaDiffer.LoadFromFile(Path.Combine(Path.GetTempPath(), "non-existent.json")));
    }

    [Fact]
    public void Differ_ArgumentNull_Throws()
    {
        var snap = new SchemaSnapshot();
        Assert.Throws<ArgumentNullException>(() => SchemaDiffer.Compute(null!, snap));
        Assert.Throws<ArgumentNullException>(() => SchemaDiffer.Compute(snap, null!));
    }

    [Fact]
    public void Result_SchemaDiff_SerializesOnlyWhenNotNull()
    {
        var result = new AnalysisResult
        {
            TotalObjects = 1,
            FieldStatistics = new List<FieldStatistic>()
        };
        // 未设置 SchemaDiff 时，序列化应忽略
        string json = JsonSerializer.Serialize(result, s_jsonOptions);
        Assert.DoesNotContain("schemaDiff", json, StringComparison.OrdinalIgnoreCase);

        // 设置后应出现
        result.SchemaDiff = new SchemaDiff { HasBreakingChanges = false };
        json = JsonSerializer.Serialize(result, s_jsonOptions);
        Assert.Contains("schemaDiff", json, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static AnalysisResult BuildResult(IEnumerable<FieldStatistic> stats)
    {
        return new AnalysisResult
        {
            TotalObjects = 10,
            FieldStatistics = stats.ToList()
        };
    }

    private static FieldStatistic MakeField(
        string name,
        string primaryType,
        string? semantic = null,
        double coverage = 1.0,
        double? minNumber = null,
        double? maxNumber = null,
        string? outlierMethod = null,
        double? outlierRatio = null,
        DateTimeOffset? minDateTime = null,
        DateTimeOffset? maxDateTime = null)
    {
        return new FieldStatistic
        {
            FieldName = name,
            PrimaryType = primaryType,
            SemanticType = semantic,
            Coverage = coverage,
            Count = (long)(coverage * 10),
            MinNumber = minNumber,
            MaxNumber = maxNumber,
            OutlierMethod = outlierMethod,
            OutlierRatio = outlierRatio,
            MinDateTime = minDateTime,
            MaxDateTime = maxDateTime
        };
    }

    private static SchemaSnapshotField MakeSnap(
        string path,
        string primaryType,
        double coverage,
        bool nullable = false,
        string? semantic = null,
        double? minNumber = null,
        double? maxNumber = null,
        string? outlierMethod = null,
        double? outlierRatio = null,
        DateTimeOffset? minDateTime = null,
        DateTimeOffset? maxDateTime = null)
    {
        return new SchemaSnapshotField
        {
            Path = path,
            PrimaryType = primaryType,
            Coverage = coverage,
            Nullable = nullable,
            SemanticType = semantic,
            MinNumber = minNumber,
            MaxNumber = maxNumber,
            OutlierMethod = outlierMethod,
            OutlierRatio = outlierRatio,
            MinDateTime = minDateTime,
            MaxDateTime = maxDateTime
        };
    }
}
