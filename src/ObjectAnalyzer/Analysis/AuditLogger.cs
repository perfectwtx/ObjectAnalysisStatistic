using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 单条审计日志记录（NDJSON 格式，每行一条 JSON 对象）。
/// 字段设计目标：
///  - 自描述：包含 schema/version，便于后续解析与归档；
///  - 不含 PII：仅记录输入文件的 SHA-256、记录数与字段名，绝不记录字段值；
///  - 可关联：含 runId 串联同一次分析的多条事件（start/finish/error）。
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>日志 schema 版本，便于后续解析兼容性管理。</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "object-analyzer.audit/v1";

    /// <summary>UTC 时间戳（ISO 8601，毫秒精度）。</summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    /// <summary>事件类型：analysis_start / analysis_finish / analysis_error / analysis_cancel。</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    /// <summary>本次分析运行唯一 ID（用于串联多条事件）。</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    /// <summary>操作者标识（--operator；缺省为 "anonymous"，允许运维侧回填）。</summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "anonymous";

    /// <summary>工具版本（ObjectAnalyzer 版本）。</summary>
    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; set; } = "";

    /// <summary>输入来源描述：文件路径 / db://provider?query / 派生表达式后缀（不含敏感值）。</summary>
    [JsonPropertyName("inputSource")]
    public string InputSource { get; set; } = "";

    /// <summary>输入内容的 SHA-256 哈希（hex），用于溯源；不记录原文。空时表示数据库直连等无法哈希的来源。</summary>
    [JsonPropertyName("inputHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputHash { get; set; }

    /// <summary>输入字节数（仅当 inputHash 非空时存在）。</summary>
    [JsonPropertyName("inputBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InputBytes { get; set; }

    /// <summary>本次分析使用的关键选项快照（不含敏感值；密钥/PEM 等不写入）。</summary>
    [JsonPropertyName("options")]
    public AuditOptionsSnapshot Options { get; set; } = new();

    /// <summary>分析完成时的结果摘要（仅 start 不含）。</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuditResultSummary? Result { get; set; }

    /// <summary>错误或取消的描述（仅 error/cancel 事件存在；不包含 PII）。</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>退出码（仅 finish/error 事件存在）。</summary>
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }
}

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

/// <summary>单个 transform 字段的脱敏描述（仅记录字段名 + 转换类型）。</summary>
public sealed class AuditTransformRef
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

/// <summary>分析完成时的关键统计指标（不含值）。</summary>
public sealed class AuditResultSummary
{
    [JsonPropertyName("totalObjects")] public long TotalObjects { get; set; }
    [JsonPropertyName("totalUniqueObjects")] public long TotalUniqueObjects { get; set; }
    [JsonPropertyName("totalFields")] public int TotalFields { get; set; }
    [JsonPropertyName("qualityViolationCount")] public int QualityViolationCount { get; set; }
    [JsonPropertyName("piiFieldCount")] public int PiiFieldCount { get; set; }
    [JsonPropertyName("piiComplianceLevel")] public string? PiiComplianceLevel { get; set; }
    [JsonPropertyName("schemaBreakingChangeCount")] public int SchemaBreakingChangeCount { get; set; }
    [JsonPropertyName("outlierOffenderCount")] public int OutlierOffenderCount { get; set; }
    [JsonPropertyName("transformFailureCount")] public long TransformFailureCount { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
}

/// <summary>
/// 审计日志写入器：以 JSON Lines (NDJSON) 格式向指定文件追加写入。
/// 所有写操作同步落盘（AutoFlush + Flush）以保证审计数据不会因进程崩溃而丢失。
/// 不使用异步流是为了简化并发场景下的顺序保证：每次 Write 都立即可见于文件。
/// </summary>
public sealed class AuditLogger : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public string LogPath { get; }

    /// <summary>创建追加式 AuditLogger；若文件不存在则创建，存在则追加。</summary>
    public AuditLogger(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            throw new ArgumentException("审计日志路径不能为空。", nameof(logPath));

        LogPath = logPath;
        string? dir = System.IO.Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        // FileShare.Read 允许其它进程同时读取（典型场景：审计聚合器/日志收集器）
        var stream = new System.IO.FileStream(
            logPath,
            System.IO.FileMode.Append,
            System.IO.FileAccess.Write,
            System.IO.FileShare.Read,
            bufferSize: 4096,
            useAsync: false);
        _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <summary>同步写入一条审计事件；内部串行化保证多线程下条目顺序。</summary>
    public void Write(AuditLogEntry entry)
    {
        if (_disposed) return;
        string line = JsonSerializer.Serialize(entry, s_jsonOptions);
        lock (_lock)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    /// <summary>
    /// 计算输入文件的 SHA-256 哈希（hex）。流式读取以支持大文件。
    /// 数据库直连等无文件输入时返回 null。
    /// </summary>
    public static string? ComputeFileHash(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;
        using var fs = System.IO.File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>获取输入文件字节数；不可访问时返回 null。</summary>
    public static long? GetFileSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;
        return new System.IO.FileInfo(path).Length;
    }

    /// <summary>从 AnalysisOptions + rules 构建 options 快照（脱敏）。</summary>
    public static AuditOptionsSnapshot Snapshot(AnalysisOptions options, AnalysisRules? rules)
    {
        var snap = new AuditOptionsSnapshot
        {
            Flatten = options.Flatten,
            Fields = options.Fields,
            Filter = options.Filter,
            RulesFile = options.RulesFile,
            SampleReservoir = options.SampleReservoir,
            SampleStep = options.SampleStep,
            MaxValuesToShow = options.MaxValuesToShow,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            RootPath = options.RootPath,
            RecordPath = options.RecordPath,
            PiiOff = options.PiiOff,
            FailOnPiiLevel = options.FailOnPiiLevel,
            FailOnOutlierRatio = options.FailOnOutlierRatio,
            FailOnViolations = options.FailOnViolations,
            AllowBreakingSchemaChanges = options.AllowBreakingSchemaChanges,
            ExportSchema = options.ExportSchema,
            DiffSchemaBaseline = options.DiffSchemaBaseline,
            DbProvider = options.DbProvider,
            OutputPath = options.OutputPath,
            ViolationsPath = options.ViolationsPath,
            QualityPath = options.QualityPath,
            PiiPath = options.PiiPath,
            SchemaSnapshotPath = options.SchemaSnapshotPath,
            SchemaDiffReportPath = options.SchemaDiffReportPath
        };

        if (rules?.Fields != null)
        {
            var transforms = new List<AuditTransformRef>();
            foreach (var (fieldName, rule) in rules.Fields)
            {
                if (rule.Transform == null || string.IsNullOrEmpty(rule.Transform.Type))
                    continue;
                transforms.Add(new AuditTransformRef
                {
                    Field = fieldName,
                    Type = rule.Transform.Type.Trim().ToLowerInvariant()
                });
            }
            if (transforms.Count > 0)
                snap.Transforms = transforms;
        }

        return snap;
    }

    /// <summary>从 AnalysisResult 构建结果摘要（不含 PII 值）。</summary>
    public static AuditResultSummary Summarize(AnalysisResult result, StopwatchLike? sw = null)
    {
        var summary = new AuditResultSummary
        {
            TotalObjects = result.TotalObjects,
            TotalUniqueObjects = result.TotalUniqueObjects,
            TotalFields = result.FieldStatistics.Count,
            QualityViolationCount = result.QualityViolations.Count
        };

        try
        {
            var piiReport = PiiReport.Build(result);
            summary.PiiFieldCount = piiReport.PiiFieldCount;
            summary.PiiComplianceLevel = piiReport.ComplianceLevel;
        }
        catch
        {
            // 审计不应让主流程因 PII 计算失败而崩
        }

        if (result.SchemaDiff != null)
        {
            summary.SchemaBreakingChangeCount = result.SchemaDiff.ChangedFields.Count(c => c.Breaking);
        }

        long failures = 0;
        foreach (var f in result.FieldStatistics)
            failures += f.TransformFailureCount;
        summary.TransformFailureCount = failures;

        if (sw != null)
            summary.ElapsedMs = sw.ElapsedMs;

        return summary;
    }

    public static string FormatTimestamp(DateTime utc)
        => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string NewRunId()
        => Guid.NewGuid().ToString("N");
}

/// <summary>
/// 简易秒表包装，便于在 Run 之外也能传入 AuditLogger（避免直接依赖 Stopwatch 增加耦合）。
/// </summary>
public sealed class StopwatchLike
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public long ElapsedMs => _sw.ElapsedMilliseconds;
    public void Restart() => _sw.Restart();
}
