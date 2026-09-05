namespace ObjectAnalyzer.Tests;

using System.Text.Json;
using ObjectAnalyzer.Analysis;

/// <summary>
/// 质量检查 v2 测试：minValue/maxValue、required、pattern、unique、nullRateMax
/// 与数据集期望（minRowCount / maxDuplicateRate），以及 --violations 导出与 --fail-on-violations 门禁集成。
/// </summary>

public class QualityChecksTests
{
    // ──────────────────────────────────────────────
    // minValue / maxValue（热路径精确计数）
    // ──────────────────────────────────────────────

    [Fact]
    public void Range_ViolationCounted_Exactly_EvenWhenValueCountsTruncated()
    {
        // 2000 个不同数值 → 唯一值必然截断，但范围违规计数走热路径必须精确
        const int n = 2000;
        var objects = Enumerable.Range(0, n)
            .Select(i => new Dictionary<string, object?> { ["age"] = (double)i })
            .ToList();
        var rules = new AnalysisRules();
        rules.Fields["age"] = new FieldRule { MinValue = 0, MaxValue = 199 };

        var result = Analyze(objects, rules);
        var stat = Stat(result, "age");

        Assert.True(stat.ValueCountsTruncated);           // 确认截断场景成立
        Assert.Equal(1800, stat.RangeViolationCount);     // 200..1999 越界

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Range);
        Assert.Equal("age", violation.Field);
        Assert.Equal(1800, violation.Count);
        Assert.False(violation.Approximate);
        Assert.InRange(violation.Samples.Count, 1, QualityChecks.MaxSamples);
    }

    [Fact]
    public void Range_WithinBounds_NoViolation()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["age"] = 30 }, new() { ["age"] = 40 }
        };
        var rules = new AnalysisRules();
        rules.Fields["age"] = new FieldRule { MinValue = 0, MaxValue = 150 };

        var result = Analyze(objects, rules);

        Assert.Equal(0, result.QualityViolations.Count(v => v.Check == QualityCheckTypes.Range));
        Assert.DoesNotContain(result.QualityViolations, v => v.Field == "age");
    }

    [Fact]
    public void Range_StringValues_IgnoredByNumericBounds()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = "abc" }, new() { ["v"] = 5 }
        };
        var rules = new AnalysisRules();
        rules.Fields["v"] = new FieldRule { MinValue = 0, MaxValue = 10 };

        var result = Analyze(objects, rules);

        Assert.Equal(0, result.QualityViolations.Count(v => v.Check == QualityCheckTypes.Range));
    }

    // ──────────────────────────────────────────────
    // required / nullRateMax
    // ──────────────────────────────────────────────

    [Fact]
    public void Required_Fails_On_Null_And_Missing()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["age"] = 20 },
            new() { ["age"] = null },
            new() { ["other"] = 1 } // age 完全缺失
        };
        var rules = new AnalysisRules();
        rules.Fields["age"] = new FieldRule { Required = true };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Required);
        Assert.Equal("age", violation.Field);
        Assert.Equal(2, violation.Count); // 1 null + 1 缺失
    }

    [Fact]
    public void NullRateMax_BoundaryPasses_ExceedingFails()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 10; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["v"] = i == 0 ? null : (object)(double)i });
        }

        var passRules = new AnalysisRules();
        passRules.Fields["v"] = new FieldRule { NullRateMax = 0.10 };
        var passResult = Analyze(objects, passRules);
        Assert.DoesNotContain(passResult.QualityViolations, v => v.Check == QualityCheckTypes.NullRate);

        var failRules = new AnalysisRules();
        failRules.Fields["v"] = new FieldRule { NullRateMax = 0.05 };
        var failResult = Analyze(objects, failRules);
        var violation = failResult.QualityViolations.Single(v => v.Check == QualityCheckTypes.NullRate);
        Assert.Equal(1, violation.Count);
        Assert.Contains("超过上限", violation.Message);
    }

    // ──────────────────────────────────────────────
    // pattern
    // ──────────────────────────────────────────────

    [Fact]
    public void Pattern_CountsMismatchingOccurrences()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["email"] = "a@b.com" },
            new() { ["email"] = "a@b.com" },   // 合法值出现两次
            new() { ["email"] = "not-an-email" },
            new() { ["email"] = null }          // null 不参与 pattern 检查
        };
        var rules = new AnalysisRules();
        rules.Fields["email"] = new FieldRule { Pattern = "^[^@]+@[^@]+$" };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Pattern);
        Assert.Equal(1, violation.Count);
        Assert.Equal(["not-an-email"], violation.Samples);
    }

    [Fact]
    public void Pattern_Truncation_MarksApproximate()
    {
        var objects = Enumerable.Range(0, 2500)
            .Select(i => new Dictionary<string, object?> { ["code"] = $"BAD-{i}" })
            .ToList();
        var rules = new AnalysisRules();
        rules.Fields["code"] = new FieldRule { Pattern = "^OK-" };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Pattern);
        Assert.True(violation.Approximate);
        Assert.True(violation.Count > 0);
    }

    // ──────────────────────────────────────────────
    // unique
    // ──────────────────────────────────────────────

    [Fact]
    public void Unique_DuplicatesDetected_ExactWithSamples()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "1" },
            new() { ["id"] = "1" },
            new() { ["id"] = "2" },
            new() { ["id"] = "2" },
            new() { ["id"] = "3" },
            new() { ["id"] = null } // null 不参与唯一性
        };
        var rules = new AnalysisRules();
        rules.Fields["id"] = new FieldRule { Unique = true };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Unique);
        Assert.Equal(2, violation.Count); // "1" 与 "2" 各重复一次
        Assert.False(violation.Approximate);
        Assert.Contains(violation.Samples, s => s.StartsWith("1×", StringComparison.Ordinal));
        Assert.Contains(violation.Samples, s => s.StartsWith("2×", StringComparison.Ordinal));
    }

    [Fact]
    public void Unique_Truncated_UsesHyperLogLogApproximation()
    {
        const int uniques = 2500;
        var objects = new List<Dictionary<string, object?>>(uniques * 2);
        for (int i = 0; i < uniques; i++)
        {
            string id = $"u-{i}";
            objects.Add(new Dictionary<string, object?> { ["userId"] = id });
            objects.Add(new Dictionary<string, object?> { ["userId"] = id }); // 全部重复一次
        }

        var rules = new AnalysisRules();
        rules.Fields["userId"] = new FieldRule { Unique = true };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Unique);
        Assert.True(violation.Approximate);
        Assert.InRange(violation.Count, uniques / 2, uniques * 3 / 2); // HLL ±~50% 宽松界，真值 uniques
    }

    // ──────────────────────────────────────────────
    // 数据集期望
    // ──────────────────────────────────────────────

    [Fact]
    public void Expectations_MinRowCount_FailsWhenBelow()
    {
        var objects = new List<Dictionary<string, object?>> { new() { ["v"] = 1 } };
        var rules = new AnalysisRules
        {
            Expectations = new AnalysisExpectations { MinRowCount = 1000 }
        };

        var result = Analyze(objects, rules);

        var violation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.RowCount);
        Assert.Equal("", violation.Field);
        Assert.Equal(1, violation.Count);
        Assert.Contains("1000", violation.Message);
    }

    [Fact]
    public void Expectations_MaxDuplicateRate_FailsAndPasses()
    {
        var dupObjects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = 1 },
            new() { ["v"] = 1 }, // 50% 重复率
            new() { ["v"] = 2 },
            new() { ["v"] = 3 }
        };

        var failRules = new AnalysisRules
        {
            Expectations = new AnalysisExpectations { MaxDuplicateRate = 0.01 }
        };
        var failResult = Analyze(dupObjects, failRules);
        var violation = failResult.QualityViolations.Single(v => v.Check == QualityCheckTypes.DuplicateRate);
        Assert.Equal(1, violation.Count);
        Assert.Contains("重复率", violation.Message);

        var passRules = new AnalysisRules
        {
            Expectations = new AnalysisExpectations { MaxDuplicateRate = 0.5 }
        };
        var passResult = Analyze(dupObjects, passRules);
        Assert.DoesNotContain(passResult.QualityViolations, v => v.Check == QualityCheckTypes.DuplicateRate);
    }

    // ──────────────────────────────────────────────
    // 导出与门禁集成
    // ──────────────────────────────────────────────

    [Fact]
    public void ViolationExporter_NewSchema_CoversAllDimensions()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["gender"] = "Unknown", ["email"] = "bad" },
            new() { ["gender"] = "Male" }
        };
        var rules = new AnalysisRules();
        rules.Fields["gender"] = new FieldRule
        {
            EnumValues = new List<JsonElement> { JsonSerializer.Deserialize<JsonElement>("\"Male\"")! }
        };
        rules.Fields["email"] = new FieldRule { Pattern = "^[^@]+@[^@]+$" };
        rules.Expectations = new AnalysisExpectations { MinRowCount = 100 };

        var result = Analyze(objects, rules);
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            ViolationExporter.Save(result, output, redactPii: false);
            using var doc = JsonDocument.Parse(File.ReadAllText(output));
            var root = doc.RootElement;

            Assert.Equal(3, root.GetProperty("summary").GetProperty("totalViolations").GetInt32());

            var fields = root.GetProperty("fieldViolations");
            Assert.Equal(2, fields.GetArrayLength());
            Assert.Contains(fields.EnumerateArray(),
                e => e.GetProperty("check").GetString() == "enum"
                    && e.TryGetProperty("samples", out var s)
                    && s.GetArrayLength() > 0);
            Assert.Contains(fields.EnumerateArray(),
                e => e.GetProperty("check").GetString() == "pattern");

            var dataset = root.GetProperty("datasetViolations");
            Assert.Equal(1, dataset.GetArrayLength());
            Assert.Equal("rowCount", dataset[0].GetProperty("check").GetString());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void DataQualityReport_IncludesChecks_AndDatasetViolations()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["gender"] = "Bad", ["email"] = "ok@example.com" }
        };
        var rules = new AnalysisRules();
        rules.Fields["gender"] = new FieldRule
        {
            EnumValues = new List<JsonElement> { JsonSerializer.Deserialize<JsonElement>("\"Good\"")! }
        };
        rules.Fields["email"] = new FieldRule { Pattern = "^[^@]+@[^@]+$" }; // 合法，无违规
        rules.Expectations = new AnalysisExpectations { MinRowCount = 50 };

        var report = DataQualityReport.Build(Analyze(objects, rules));

        Assert.False(report.Passed);
        var genderRow = report.Fields.Single(f => f.Field == "gender");
        Assert.Equal(1, genderRow.ViolationCount);      // 枚举逐值列表保持旧语义
        Assert.Equal("Bad", genderRow.Violations[0]);
        Assert.Empty(genderRow.Checks);                 // pattern 字段合法 → 无 v2 条目
        Assert.Single(report.DatasetViolations);        // 行数期望（数据集条目按 1 计）
        Assert.Equal(2, report.TotalViolationCount);    // 1 枚举 + 1 数据集期望
    }

    [Fact]
    public void AnalysisRunner_FailOnViolations_ExitsWith3_ForPatternAndExpectation()
    {
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string rulesPath = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input,
                """[{"email":"bad"},{"email":"also-bad"}]""");
            File.WriteAllText(rulesPath,
                """
                {
                  "fields": { "email": { "pattern": "^[^@]+@[^@]+$" } },
                  "expectations": { "minRowCount": 1000 }
                }
                """);

            var options = new AnalysisOptions
            {
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Gates = { FailOnViolations = true }
            };
            Assert.Equal(3, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(rulesPath)) File.Delete(rulesPath);
        }
    }

    [Fact]
    public void AnalysisRunner_QualityClean_PassesGate()
    {
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string rulesPath = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input,
                """[{"email":"a@b.com"},{"email":"c@d.com"}]""");
            File.WriteAllText(rulesPath,
                """
                {
                  "fields": { "email": { "pattern": "^[^@]+@[^@]+$", "required": true } },
                  "expectations": { "maxDuplicateRate": 0.01 }
                }
                """);

            var options = new AnalysisOptions
            {
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Gates = { FailOnViolations = true }
            };
            Assert.Equal(0, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(rulesPath)) File.Delete(rulesPath);
        }
    }

    [Fact]
    public void AnalysisRunner_InvalidPattern_FailsFast_WithExit1()
    {
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string rulesPath = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input, """[{"v":"x"}]""");
            File.WriteAllText(rulesPath,
                """{"fields": { "v": { "pattern": "[unclosed" } }}""");

            var options = new AnalysisOptions { Input = { InputPath = input }, Rules = { RulesFile = rulesPath } };
            Assert.Equal(1, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(rulesPath)) File.Delete(rulesPath);
        }
    }

    // ──────────────────────────────────────────────
    // PII 打码传播
    // ──────────────────────────────────────────────

    [Fact]
    public void PiiRedactor_Masks_QualityCheck_Samples()
    {
        // 两个值均为合法邮箱形态（会被 PII 掩码），但都不满足 ^good@ 模式 → 违规样本必被掩码
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["email"] = "bad@corp.com" },
            new() { ["email"] = "worse@corp.com" }
        };
        var rules = new AnalysisRules();
        rules.Fields["email"] = new FieldRule { Pattern = "^good@" };

        var result = Analyze(objects, rules);
        var redacted = PiiRedactor.Redact(result);

        var originalViolation = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.Pattern);
        var maskedViolation = redacted.QualityViolations.Single(v => v.Check == QualityCheckTypes.Pattern);
        Assert.Equal(2, originalViolation.Samples.Count);
        string joined = string.Join(",", maskedViolation.Samples);
        Assert.DoesNotContain("bad@corp.com", joined);
        Assert.DoesNotContain("worse@corp.com", joined);
        Assert.NotEqual((IEnumerable<string>)originalViolation.Samples, maskedViolation.Samples);
    }

    [Fact]
    public void BackwardCompat_EnumOnlyRules_ProduceSingleAggregatedEntry()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["g"] = "A" }, new() { ["g"] = "X" }, new() { ["g"] = "Y" }
        };
        var rules = new AnalysisRules();
        rules.Fields["g"] = new FieldRule
        {
            EnumValues = new List<JsonElement> { JsonSerializer.Deserialize<JsonElement>("\"A\"")! }
        };

        var result = Analyze(objects, rules);

        // v1 行为不变：逐值枚举违规仍在字段上
        var stat = Stat(result, "g");
        Assert.Equal(2, stat.EnumViolations.Count);

        // v2 新增：聚合条目供统一门禁/导出消费
        var entry = result.QualityViolations.Single(v => v.Check == QualityCheckTypes.EnumValues);
        Assert.Equal(2, entry.Count);
        Assert.Contains("X", entry.Samples);
    }

    [Fact]
    public void Serialization_RoundTrips_QualityViolations()
    {
        var objects = new List<Dictionary<string, object?>> { new() { ["v"] = 999 } };
        var rules = new AnalysisRules();
        rules.Fields["v"] = new FieldRule { MaxValue = 100 };

        var result = Analyze(objects, rules);
        string json = JsonSerializer.Serialize(result, JsonWriteOptions);
        var restored = JsonSerializer.Deserialize<AnalysisResult>(json, JsonReadOptions);

        var violation = restored!.QualityViolations.Single(v => v.Check == QualityCheckTypes.Range);
        Assert.Equal("v", violation.Field);
        Assert.Equal(1, violation.Count);
        Assert.Equal("999", violation.Samples.Single());
    }

    // ──────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static FieldStatistic Stat(AnalysisResult result, string fieldName)
        => result.FieldStatistics.First(f => f.FieldName == fieldName);
}
