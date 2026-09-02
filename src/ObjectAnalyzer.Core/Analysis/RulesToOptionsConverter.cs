using System.Text.Json.Nodes;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 规则文件 (JSON) → <see cref="AnalysisOptions"/> 的一次性转换。
/// <para>
/// 旧流程：AnalysisRules → AnalysisRuntime (+ 13 个 *Config) → AnalysisOptions (扁平)，
/// 存在"前一个类有 flatten，还有转换为后一个类的 flatten"的多层冗余。
/// </para>
/// <para>
/// 新流程：AnalysisRules.Runtime (JsonObject) → 直接写入 AnalysisOptions 对应子组，
/// 不再维护强类型中间类。
/// </para>
/// <para>
/// 优先级：调用方已经显式设置的非默认子组字段不会被 runtime 覆盖。
/// </para>
/// </summary>
public static class RulesToOptionsConverter
{
    /// <summary>
    /// 把 <paramref name="rules"/> 的 runtime 节点合并到 <paramref name="options"/>。
    /// 仅当子组字段仍为"内置默认"时才会被 runtime 覆盖。
    /// </summary>
    public static void Apply(AnalysisOptions options, AnalysisRules? rules)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (rules?.Runtime == null) return;

        var rt = rules.Runtime;

        // ── 解析行为 ──
        ApplyBool(rt, "flatten", options.Parse, p => p.Flatten, (p, v) => p.Flatten = v, defaultValue: true);
        ApplyBool(rt, "csvInferNumbers", options.Parse, p => p.CsvInferNumbers, (p, v) => p.CsvInferNumbers = v, defaultValue: true);
        ApplyInt(rt, "maxValuesToShow", options.Parse, p => p.MaxValuesToShow, 5, (p, v) => p.MaxValuesToShow = v);
        ApplyInt(rt, "maxParallelism", options.Parse, p => p.MaxDegreeOfParallelism, -1, (p, v) => p.MaxDegreeOfParallelism = v);
        ApplyBool(rt, "allowUnknownRules", options.Rules, p => p.AllowUnknownRules, (p, v) => p.AllowUnknownRules = v);

        // ── 字段选择 / 过滤 ──
        if (TryGetArray(rt, "selectedFields") is { Count: > 0 } && string.IsNullOrEmpty(options.FieldSelection.Fields))
        {
            options.FieldSelection.Fields = string.Join(",", TryGetArray(rt, "selectedFields")!);
        }
        ApplyString(rt, "filter", options.FieldSelection, p => p.Filter, (p, v) => p.Filter = v);

        // ── 采样 ──
        if (rt["sampling"] is JsonObject sampling)
        {
            ApplyInt(sampling, "reservoir", options.Sampling, s => s.SampleReservoir, 0, (s, v) => s.SampleReservoir = v);
            ApplyInt(sampling, "step", options.Sampling, s => s.SampleStep, 0, (s, v) => s.SampleStep = v);
        }

        // ── JSONPath ──
        if (rt["jsonPath"] is JsonObject jsonPath)
        {
            ApplyString(jsonPath, "root", options.Input, i => i.RootPath, (i, v) => i.RootPath = v);
            ApplyString(jsonPath, "record", options.Input, i => i.RecordPath, (i, v) => i.RecordPath = v);
        }

        // ── Schema 演进 ──
        if (rt["schema"] is JsonObject schema)
        {
            ApplyString(schema, "snapshot", options.Schema, s => s.SchemaSnapshotPath, (s, v) => s.SchemaSnapshotPath = v);
            ApplyString(schema, "diffBaseline", options.Schema, s => s.DiffSchemaBaseline, (s, v) => s.DiffSchemaBaseline = v);
            ApplyString(schema, "diffReport", options.Schema, s => s.SchemaDiffReportPath, (s, v) => s.SchemaDiffReportPath = v);
            ApplyBool(schema, "allowBreakingChanges", options.Gates, g => g.AllowBreakingSchemaChanges, (g, v) => g.AllowBreakingSchemaChanges = v, defaultValue: true);
        }

        // ── 分布漂移 ──
        if (rt["distribution"] is JsonObject dist)
        {
            ApplyString(dist, "snapshot", options.Distribution, d => d.DistributionSnapshotPath, (d, v) => d.DistributionSnapshotPath = v);
            ApplyString(dist, "diffBaseline", options.Distribution, d => d.DiffDistributionBaseline, (d, v) => d.DiffDistributionBaseline = v);
            ApplyString(dist, "diffReport", options.Distribution, d => d.DistributionDiffReportPath, (d, v) => d.DistributionDiffReportPath = v);
            ApplyDouble(dist, "lowConfidenceWidth", options.Sampling, s => s.LowConfidenceWidth, 0.2, (s, v) => s.LowConfidenceWidth = v);
        }

        // ── PII / 合规 ──
        if (rt["pii"] is JsonObject pii)
        {
            ApplyBool(pii, "off", options.Pii, p => p.PiiOff, (p, v) => p.PiiOff = v);
            ApplyString(pii, "report", options.Pii, p => p.PiiPath, (p, v) => p.PiiPath = v);
            ApplyString(pii, "failOnLevel", options.Gates, g => g.FailOnPiiLevel, (g, v) => g.FailOnPiiLevel = v);
        }

        // ── 门禁 ──
        if (rt["gates"] is JsonObject gates)
        {
            ApplyBool(gates, "failOnViolations", options.Gates, g => g.FailOnViolations, (g, v) => g.FailOnViolations = v);
            ApplyNullableDouble(gates, "failOnOutlierRatio", options.Gates, g => g.FailOnOutlierRatio, (g, v) => g.FailOnOutlierRatio = v);
        }

        // ── 深度分析 ──
        if (rt["deepAnalysis"] is JsonObject deep)
        {
            ApplyBool(deep, "correlation", options.Advanced, a => a.EnableCorrelation, (a, v) => a.EnableCorrelation = v);
            ApplyBool(deep, "stringPatterns", options.Advanced, a => a.EnableStringPatterns, (a, v) => a.EnableStringPatterns = v);
            ApplyBool(deep, "unicodeHygiene", options.Advanced, a => a.EnableUnicodeHygiene, (a, v) => a.EnableUnicodeHygiene = v);
            ApplyBool(deep, "timeSeries", options.Advanced, a => a.EnableTimeSeries, (a, v) => a.EnableTimeSeries = v);
            ApplyBool(deep, "distributionSnapshot", options.Advanced, a => a.EnableDistributionSnapshot, (a, v) => a.EnableDistributionSnapshot = v);
            ApplyString(deep, "fuzzyDedupField", options.Advanced, a => a.FuzzyDedupField, (a, v) => a.FuzzyDedupField = v);
        }

        // ── 检查点 / 续跑 / 基线 ──
        if (rt["checkpoint"] is JsonObject checkpoint)
        {
            ApplyString(checkpoint, "path", options.Incremental, c => c.CheckpointPath, (c, v) => c.CheckpointPath = v);
            ApplyString(checkpoint, "resumeFrom", options.Incremental, c => c.ResumeFrom, (c, v) => c.ResumeFrom = v);
            ApplyString(checkpoint, "baseline", options.Incremental, c => c.BaselinePath, (c, v) => c.BaselinePath = v);
        }

        // ── 内存预算 ──
        if (rt["memory"] is JsonObject memory)
        {
            ApplyLong(memory, "maxMB", options.Memory, m => m.MaxMemoryMB, 0, (m, v) => m.MaxMemoryMB = v);
            if (memory["mode"] is JsonValue modeVal && modeVal.TryGetValue<string>(out var mode)
                && Enum.TryParse<MaxMemoryMode>(mode, ignoreCase: true, out var parsed)
                && options.Memory.MaxMemoryMode == MaxMemoryMode.Soft)
            {
                options.Memory.MaxMemoryMode = parsed;
            }
        }

        // ── 进度 ──
        if (rt["progress"] is JsonObject progress)
        {
            ApplyBool(progress, "enabled", options.Progress, p => p.ShowProgress, (p, v) => p.ShowProgress = v, defaultValue: true);
        }

        // ── 审计 ──
        if (rt["audit"] is JsonObject audit)
        {
            ApplyString(audit, "log", options.Audit, a => a.AuditLogPath, (a, v) => a.AuditLogPath = v);
            ApplyString(audit, "operator", options.Audit, a => a.Operator, (a, v) => a.Operator = v, defaultValue: "anonymous");
        }

        // ── 输出 ──
        if (rt["outputs"] is JsonObject outputs)
        {
            ApplyString(outputs, "primary", options.Report, r => r.OutputPath, (r, v) => r.OutputPath = v);
            ApplyString(outputs, "violations", options.Report, r => r.ViolationsPath, (r, v) => r.ViolationsPath = v);
            ApplyString(outputs, "quality", options.Report, r => r.QualityPath, (r, v) => r.QualityPath = v);
            if (outputs["schemaCode"] is JsonValue scVal && scVal.TryGetValue<string>(out var schemaCode)
                && !string.IsNullOrWhiteSpace(schemaCode)
                && string.IsNullOrWhiteSpace(options.Report.ExportSchema))
            {
                int colonIdx = schemaCode.IndexOf(':');
                if (colonIdx > 0 && colonIdx < schemaCode.Length - 1)
                {
                    options.Report.ExportSchema = schemaCode.Substring(0, colonIdx);
                    options.Report.SchemaOutputPath = schemaCode.Substring(colonIdx + 1);
                }
                else
                {
                    options.Report.ExportSchema = schemaCode;
                }
            }
        }
    }

    // ── 通用读 + 覆盖辅助方法 ──

    private static void ApplyBool<T>(JsonObject src, string key, T target, Func<T, bool> getter, Action<T, bool> setter, bool defaultValue = false)
    {
        if (src[key] is JsonValue v && v.TryGetValue<bool>(out var val) && getter(target) == defaultValue)
        {
            setter(target, val);
        }
    }

    private static void ApplyString<T>(JsonObject src, string key, T target, Func<T, string?> getter, Action<T, string> setter, string? defaultValue = null)
    {
        if (src[key] is JsonValue v && v.TryGetValue<string>(out var val) && !string.IsNullOrWhiteSpace(val))
        {
            var current = getter(target);
            if (string.IsNullOrWhiteSpace(current) || current == defaultValue)
            {
                setter(target, val);
            }
        }
    }

    private static void ApplyInt<T>(JsonObject src, string key, T target, Func<T, int> getter, int defaultValue, Action<T, int> setter)
    {
        if (src[key] is JsonValue v && v.TryGetValue<int>(out var val) && getter(target) == defaultValue)
        {
            setter(target, val);
        }
    }

    private static void ApplyLong<T>(JsonObject src, string key, T target, Func<T, long> getter, long defaultValue, Action<T, long> setter)
    {
        if (src[key] is JsonValue v && v.TryGetValue<long>(out var val) && getter(target) == defaultValue)
        {
            setter(target, val);
        }
    }

    private static void ApplyDouble<T>(JsonObject src, string key, T target, Func<T, double> getter, double defaultValue, Action<T, double> setter)
    {
        if (src[key] is JsonValue v && v.TryGetValue<double>(out var val) && getter(target) == defaultValue)
        {
            setter(target, val);
        }
    }

    private static void ApplyNullableDouble<T>(JsonObject src, string key, T target, Func<T, double?> getter, Action<T, double?> setter)
    {
        if (src[key] is JsonValue v && v.TryGetValue<double>(out var val) && !getter(target).HasValue)
        {
            setter(target, val);
        }
    }

    private static List<string>? TryGetArray(JsonObject src, string key)
    {
        if (src[key] is JsonArray arr)
        {
            return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        return null;
    }
}
