using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 选项快照：仅记录与"谁/如何/为什么"审计相关的非敏感字段；
/// 规则文件路径、采样参数、合规门禁等写入；密钥、PEM、签名等敏感值绝不写入。
/// </summary>
public sealed class AuditOptionsSnapshot
{
    [JsonPropertyName("inputFormat")] public string? InputFormat { get; set; }
    [JsonPropertyName("flatten")] public bool Flatten { get; set; }
    [JsonPropertyName("fields")] public string? Fields { get; set; }
    [JsonPropertyName("filter")] public string? Filter { get; set; }
    [JsonPropertyName("rulesFile")] public string? RulesFile { get; set; }
    [JsonPropertyName("sampleReservoir")] public int SampleReservoir { get; set; }
    [JsonPropertyName("sampleStep")] public int SampleStep { get; set; }
    [JsonPropertyName("maxValuesToShow")] public int MaxValuesToShow { get; set; }
    [JsonPropertyName("maxDegreeOfParallelism")] public int MaxDegreeOfParallelism { get; set; }
    [JsonPropertyName("rootPath")] public string? RootPath { get; set; }
    [JsonPropertyName("recordPath")] public string? RecordPath { get; set; }
    [JsonPropertyName("piiOff")] public bool PiiOff { get; set; }
    [JsonPropertyName("failOnPiiLevel")] public string? FailOnPiiLevel { get; set; }
    [JsonPropertyName("failOnOutlierRatio")] public double? FailOnOutlierRatio { get; set; }
    [JsonPropertyName("failOnViolations")] public bool FailOnViolations { get; set; }
    [JsonPropertyName("allowBreakingSchemaChanges")] public bool AllowBreakingSchemaChanges { get; set; }
    [JsonPropertyName("exportSchema")] public string? ExportSchema { get; set; }
    [JsonPropertyName("diffSchemaBaseline")] public string? DiffSchemaBaseline { get; set; }
    [JsonPropertyName("dbProvider")] public string? DbProvider { get; set; }
    [JsonPropertyName("outputPath")] public string? OutputPath { get; set; }
    [JsonPropertyName("violationsPath")] public string? ViolationsPath { get; set; }
    [JsonPropertyName("qualityPath")] public string? QualityPath { get; set; }
    [JsonPropertyName("piiPath")] public string? PiiPath { get; set; }
    [JsonPropertyName("schemaSnapshotPath")] public string? SchemaSnapshotPath { get; set; }
    [JsonPropertyName("schemaDiffReportPath")] public string? SchemaDiffReportPath { get; set; }

    /// <summary>已声明的 transform 字段名与类型（不含 key/iv/私钥/公钥/签名等敏感值）。</summary>
    [JsonPropertyName("transforms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AuditTransformRef>? Transforms { get; set; }
}
