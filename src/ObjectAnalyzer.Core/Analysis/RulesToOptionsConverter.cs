using System.Text.Json.Nodes;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

/// <summary>规则文件 runtime → AnalysisOptions 的单一转换入口。</summary>
public static class RulesToOptionsConverter
{
    public static readonly IReadOnlyDictionary<string, string[]?> KnownRuntimeKeys =
        new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase)
        {
            ["flatten"] = null,
            ["csvInferNumbers"] = null,
            ["maxValuesToShow"] = null,
            ["maxTrackedValues"] = null,
            ["maxParallelism"] = null,
            ["allowUnknownRules"] = null,
            ["selectedFields"] = null,
            ["filter"] = null,
            ["sampling"] = ["reservoir", "step"],
            ["jsonPath"] = ["root", "record"],
            ["schema"] = ["snapshot", "diffBaseline", "diffReport", "allowBreakingChanges"],
            ["distribution"] = ["snapshot", "diffBaseline", "diffReport", "lowConfidenceWidth"],
            ["pii"] = ["off", "report", "failOnLevel"],
            ["gates"] = ["failOnViolations", "failOnOutlierRatio", "failOnOverallScoreBelow", "failOnCompletenessBelow", "failOnValidityBelow", "failOnUniquenessBelow", "failOnConsistencyBelow", "failOnAnomalyControlBelow"],
            ["deepAnalysis"] = ["correlation", "stringPatterns", "unicodeHygiene", "timeSeries", "distributionSnapshot", "fuzzyDedupField"],
            ["checkpoint"] = ["path", "resumeFrom", "baseline"],
            ["memory"] = ["maxMB", "mode"],
            ["progress"] = ["enabled"],
            ["audit"] = ["log", "operator"],
            ["outputs"] = ["primary", "violations", "quality", "schemaCode"],
        };

    public static void Apply(AnalysisOptions options, AnalysisRules? rules)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (rules?.Runtime == null) return;
        var rt = rules.Runtime;

        ApplyBool(rt, "flatten", options.Parse, p => p.Flatten, (p, v) => p.Flatten = v, true);
        ApplyBool(rt, "csvInferNumbers", options.Parse, p => p.CsvInferNumbers, (p, v) => p.CsvInferNumbers = v, true);
        ApplyInt(rt, "maxValuesToShow", options.Parse, p => p.MaxValuesToShow, 5, (p, v) => p.MaxValuesToShow = v);
        ApplyInt(rt, "maxTrackedValues", options.Parse, p => p.MaxTrackedValues, 1000, (p, v) => p.MaxTrackedValues = v);
        ApplyInt(rt, "maxParallelism", options.Parse, p => p.MaxDegreeOfParallelism, -1, (p, v) => p.MaxDegreeOfParallelism = v);
        ApplyBool(rt, "allowUnknownRules", options.Rules, p => p.AllowUnknownRules, (p, v) => p.AllowUnknownRules = v);

        if (TryGetArray(rt, "selectedFields") is { Count: > 0 } selected && string.IsNullOrEmpty(options.FieldSelection.Fields))
            options.FieldSelection.Fields = string.Join(",", selected);
        ApplyString(rt, "filter", options.FieldSelection, p => p.Filter, (p, v) => p.Filter = v);

        if (rt["sampling"] is JsonObject sampling)
        {
            ApplyInt(sampling, "reservoir", options.Sampling, s => s.SampleReservoir, 0, (s, v) => s.SampleReservoir = v);
            ApplyInt(sampling, "step", options.Sampling, s => s.SampleStep, 0, (s, v) => s.SampleStep = v);
        }

        if (rt["jsonPath"] is JsonObject jsonPath)
        {
            ApplyString(jsonPath, "root", options.Input, i => i.RootPath, (i, v) => i.RootPath = v);
            ApplyString(jsonPath, "record", options.Input, i => i.RecordPath, (i, v) => i.RecordPath = v);
        }

        if (rt["schema"] is JsonObject schema)
        {
            ApplyString(schema, "snapshot", options.Schema, s => s.SchemaSnapshotPath, (s, v) => s.SchemaSnapshotPath = v);
            ApplyString(schema, "diffBaseline", options.Schema, s => s.DiffSchemaBaseline, (s, v) => s.DiffSchemaBaseline = v);
            ApplyString(schema, "diffReport", options.Schema, s => s.SchemaDiffReportPath, (s, v) => s.SchemaDiffReportPath = v);
            ApplyBool(schema, "allowBreakingChanges", options.Gates, g => g.AllowBreakingSchemaChanges, (g, v) => g.AllowBreakingSchemaChanges = v, true);
        }

        if (rt["distribution"] is JsonObject dist)
        {
            ApplyString(dist, "snapshot", options.Distribution, d => d.DistributionSnapshotPath, (d, v) => d.DistributionSnapshotPath = v);
            ApplyString(dist, "diffBaseline", options.Distribution, d => d.DiffDistributionBaseline, (d, v) => d.DiffDistributionBaseline = v);
            ApplyString(dist, "diffReport", options.Distribution, d => d.DistributionDiffReportPath, (d, v) => d.DiffDistributionDiffReportPath = v);
            ApplyDouble(dist, "lowConfidenceWidth", options.Sampling, s => s.LowConfidenceWidth, 0.2, (s, v) => s.LowConfidenceWidth = v);
        }

        if (rt["pii"] is JsonObject pii)
        {
            ApplyBool(pii, "off", options.Pii, p => p.PiiOff, (p, v) => p.PiiOff = v);
            ApplyString(pii, "report", options.Pii, p => p.PiiPath, (p, v) => p.PiiPath = v);
            ApplyString(pii, "failOnLevel", options.Gates, g => g.FailOnPiiLevel, (g, v) => g.FailOnPiiLevel = v);
        }

        if (rt["gates"] is JsonObject gates)
        {
            ApplyBool(gates, "failOnViolations", options.Gates, g => g.FailOnViolations, (g, v) => g.FailOnViolations = v);
            ApplyNullableDouble(gates, "failOnOutlierRatio", options.Gates, g => g.FailOnOutlierRatio, (g, v) => g.FailOnOutlierRatio = v);
            ApplyNullableDouble(gates, "failOnOverallScoreBelow", options.Gates, g => g.FailOnOverallScoreBelow, (g, v) => g.FailOnOverallScoreBelow = v);
            ApplyNullableDouble(gates, "failOnCompletenessBelow", options.Gates, g => g.FailOnCompletenessBelow, (g, v) => g.FailOnCompletenessBelow = v);
            ApplyNullableDouble(gates, "failOnValidityBelow", options.Gates, g => g.FailOnValidityBelow, (g, v) => g.FailOnValidityBelow = v);
            ApplyNullableDouble(gates, "failOnUniquenessBelow", options.Gates, g => g.FailOnUniquenessBelow, (g, v) => g.FailOnUniquenessBelow = v);
            ApplyNullableDouble(gates, "failOnConsistencyBelow", options.Gates, g => g.FailOnConsistencyBelow, (g, v) => g.FailOnConsistencyBelow = v);
            ApplyNullableDouble(gates, "failOnAnomalyControlBelow", options.Gates, g => g.FailOnAnomalyControlBelow, (g, v) => g.FailOnAnomalyControlBelow = v);
        }

        if (rt["deepAnalysis"] is JsonObject deep)
        {
            ApplyBool(deep, "correlation", options.Advanced, a => a.EnableCorrelation, (a, v) => a.EnableCorrelation = v);
            ApplyBool(deep, "stringPatterns", options.Advanced, a => a.EnableStringPatterns, (a, v) => a.EnableStringPatterns = v);
            ApplyBool(deep, "unicodeHygiene", options.Advanced, a => a.EnableUnicodeHygiene, (a, v) => a.EnableUnicodeHygiene = v);
            ApplyBool(deep, "timeSeries", options.Advanced, a => a.EnableTimeSeries, (a, v) => a.EnableTimeSeries = v);
            ApplyBool(deep, "distributionSnapshot", options.Advanced, a => a.EnableDistributionSnapshot, (a, v) => a.EnableDistributionSnapshot = v);
            ApplyString(deep, "fuzzyDedupField", options.Advanced, a => a.FuzzyDedupField, (a, v) => a.FuzzyDedupField = v);
        }

        if (rt["checkpoint"] is JsonObject checkpoint)
        {
            ApplyString(checkpoint, "path", options.Incremental, c => c.CheckpointPath, (c, v) => c.CheckpointPath = v);
            ApplyString(checkpoint, "resumeFrom", options.Incremental, c => c.ResumeFrom, (c, v) => c.ResumeFrom = v);
            ApplyString(checkpoint, "baseline", options.Incremental, c => c.BaselinePath, (c, v) => c.BaselinePath = v);
        }

        if (rt["memory"] is JsonObject memory)
        {
            ApplyLong(memory, "maxMB", options.Memory, m => m.MaxMemoryMB, 0, (m, v) => m.MaxMemoryMB = v);
            if (memory["mode"] is JsonValue modeVal && modeVal.TryGetValue<string>(out var mode)
                && Enum.TryParse<MaxMemoryMode>(mode, true, out var parsed)
                && options.Memory.MaxMemoryMode == MaxMemoryMode.Soft)
                options.Memory.MaxMemoryMode = parsed;
        }

        if (rt["progress"] is JsonObject progress)
            ApplyBool(progress, "enabled", options.Progress, p => p.ShowProgress, (p, v) => p.ShowProgress = v, true);

        if (rt["audit"] is JsonObject audit)
        {
            ApplyString(audit, "log", options.Audit, a => a.AuditLogPath, (a, v) => a.AuditLogPath = v);
            ApplyString(audit, "operator", options.Audit, a => a.Operator, (a, v) => a.Operator = v, "anonymous");
        }

        if (rt["outputs"] is JsonObject outputs)
        {
            ApplyString(outputs, "primary", options.Report, r => r.OutputPath, (r, v) => r.OutputPath = v);
            ApplyString(outputs, "violations", options.Report, r => r.ViolationsPath, (r, v) => r.ViolationsPath = v);
            ApplyString(outputs, "quality", options.Report, r => r.QualityPath, (r, v) => r.QualityPath = v);
            if (outputs["schemaCode"] is JsonValue scVal && scVal.TryGetValue<string>(out var schemaCode)
                && !string.IsNullOrWhiteSpace(schemaCode) && string.IsNullOrWhiteSpace(options.Report.ExportSchema))
            {
                int colonIdx = schemaCode.IndexOf(':');
                if (colonIdx > 0 && colonIdx < schemaCode.Length - 1)
                {
                    options.Report.ExportSchema = schemaCode[..colonIdx];
                    options.Report.SchemaOutputPath = schemaCode[(colonIdx + 1)..];
                }
                else options.Report.ExportSchema = schemaCode;
            }
        }
    }

    private static void ApplyBool<T>(JsonObject src, string key, T target, Func<T, bool> getter, Action<T, bool> setter, bool defaultValue = false)
    { if (src[key] is JsonValue v && v.TryGetValue<bool>(out var val) && getter(target) == defaultValue) setter(target, val); }
    private static void ApplyString<T>(JsonObject src, string key, T target, Func<T, string?> getter, Action<T, string> setter, string? defaultValue = null)
    { if (src[key] is JsonValue v && v.TryGetValue<string>(out var val) && !string.IsNullOrWhiteSpace(val) && (string.IsNullOrWhiteSpace(getter(target)) || getter(target) == defaultValue)) setter(target, val); }
    private static void ApplyInt<T>(JsonObject src, string key, T target, Func<T, int> getter, int defaultValue, Action<T, int> setter)
    { if (src[key] is JsonValue v && v.TryGetValue<int>(out var val) && getter(target) == defaultValue) setter(target, val); }
    private static void ApplyLong<T>(JsonObject src, string key, T target, Func<T, long> getter, long defaultValue, Action<T, long> setter)
    { if (src[key] is JsonValue v && v.TryGetValue<long>(out var val) && getter(target) == defaultValue) setter(target, val); }
    private static void ApplyDouble<T>(JsonObject src, string key, T target, Func<T, double> getter, double defaultValue, Action<T, double> setter)
    { if (src[key] is JsonValue v && v.TryGetValue<double>(out var val) && getter(target) == defaultValue) setter(target, val); }
    private static void ApplyNullableDouble<T>(JsonObject src, string key, T target, Func<T, double?> getter, Action<T, double?> setter)
    { if (src[key] is JsonValue v && v.TryGetValue<double>(out var val) && !getter(target).HasValue) setter(target, val); }
    private static List<string>? TryGetArray(JsonObject src, string key)
    { return src[key] is JsonArray arr ? arr.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList() : null; }
}
