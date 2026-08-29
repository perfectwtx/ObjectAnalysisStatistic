using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 审计日志写入器：以 JSON Lines (NDJSON) 格式向指定文件追加写入。
/// 所有写操作同步落盘（AutoFlush + Flush）以保证审计数据不会因进程崩溃而丢失。
/// 不使用异步流是为了简化并发场景下的顺序保证：每次 Write 都立即可见于文件。
/// </summary>
public sealed class AuditLogger(string logPath) : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private bool _disposed;

    public string LogPath { get; } = string.IsNullOrWhiteSpace(logPath)
        ? throw new ArgumentException("审计日志路径不能为空。", nameof(logPath))
        : logPath;

    private readonly StreamWriter _writer = CreateWriter(logPath);

    /// <summary>创建追加式写入器；若文件不存在则创建，存在则追加。</summary>
    private static StreamWriter CreateWriter(string logPath)
    {
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
        return new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
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
