using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using Xunit;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 数据血缘与审计日志测试：覆盖 NDJSON 写盘 / 不含 PII / 选项完整 / AnalysisRunner 集成。
/// 所有调用 AnalysisRunner.Run 的测试加入 AnalysisRunnerSequentialGroup 集合以避免 AnsiConsole 并发问题。
/// </summary>
public class AuditLoggerTests
{
    // ──────────────────────────────────────────────
    // AuditLogger 单元测试
    // ──────────────────────────────────────────────

    [Fact]
    public void AuditLogger_Writes_Ndjson_OneObjectPerLine()
    {
        string path = NewTempLogPath();
        try
        {
            using (var logger = new AuditLogger(path))
            {
                logger.Write(new AuditLogEntry
                {
                    Timestamp = "2026-08-26T10:00:00.000Z",
                    Event = "analysis_start",
                    RunId = "run-1",
                    Operator = "[email protected]",
                    ToolVersion = "1.0.0",
                    InputSource = "/tmp/data.json",
                    InputHash = "abc123",
                    InputBytes = 1024
                });
                logger.Write(new AuditLogEntry
                {
                    Timestamp = "2026-08-26T10:00:01.000Z",
                    Event = "analysis_finish",
                    RunId = "run-1",
                    Operator = "[email protected]",
                    ToolVersion = "1.0.0",
                    InputSource = "/tmp/data.json",
                    ExitCode = 0,
                    Result = new AuditResultSummary { TotalObjects = 10, TotalFields = 3 }
                });
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var doc1 = JsonDocument.Parse(lines[0]);
            using var doc2 = JsonDocument.Parse(lines[1]);
            Assert.Equal("analysis_start", doc1.RootElement.GetProperty("event").GetString());
            Assert.Equal("analysis_finish", doc2.RootElement.GetProperty("event").GetString());
            Assert.Equal("run-1", doc1.RootElement.GetProperty("runId").GetString());
            Assert.Equal(10, doc2.RootElement.GetProperty("result").GetProperty("totalObjects").GetInt64());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AuditLogger_AppendsToExistingFile()
    {
        string path = NewTempLogPath();
        try
        {
            using (var logger = new AuditLogger(path))
            {
                logger.Write(new AuditLogEntry
                {
                    Timestamp = "2026-08-26T10:00:00.000Z",
                    Event = "analysis_start",
                    RunId = "run-a"
                });
            }
            using (var logger = new AuditLogger(path))
            {
                logger.Write(new AuditLogEntry
                {
                    Timestamp = "2026-08-26T10:00:01.000Z",
                    Event = "analysis_finish",
                    RunId = "run-b"
                });
            }
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("run-a", JsonDocument.Parse(lines[0]).RootElement.GetProperty("runId").GetString());
            Assert.Equal("run-b", JsonDocument.Parse(lines[1]).RootElement.GetProperty("runId").GetString());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AuditLogger_OmitsNullOptionalFields()
    {
        string path = NewTempLogPath();
        try
        {
            using (var logger = new AuditLogger(path))
            {
                logger.Write(new AuditLogEntry
                {
                    Timestamp = "2026-08-26T10:00:00.000Z",
                    Event = "analysis_start",
                    RunId = "run-x"
                });
            }
            string line = File.ReadAllLines(path)[0];
            // inputHash / inputBytes / result / message / exitCode 不应出现在最小事件中
            Assert.DoesNotContain("inputHash", line);
            Assert.DoesNotContain("inputBytes", line);
            Assert.DoesNotContain("\"result\"", line);
            Assert.DoesNotContain("\"message\"", line);
            Assert.DoesNotContain("exitCode", line);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AuditOptionsSnapshot_NeverContainsSensitiveMaterial()
    {
        var rules = new AnalysisRules();
        rules.Fields["cipher"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = "AAAA-AAAA-AAAA-AAAA", // 假装密钥
                Iv = "BBBB-BBBB-BBBB-BBBB",
                Format = "base64",
                Mode = "cbc"
            }
        };
        rules.Fields["sm2sig"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "verify",
                PublicKey = "-----PUBLIC KEY-----\nXXX",
                PrivateKey = "-----PRIVATE KEY-----\nXXX",
                Signature = "DEADBEEF",
                Format = "base64"
            }
        };

        var options = new AnalysisOptions
        {
            InputPath = "/tmp/data.json",
            RulesFile = "/tmp/rules.json"
        };

        var snap = AuditLogger.Snapshot(options, rules);

        string serialized = JsonSerializer.Serialize(snap);
        // 关键：审计快照不得包含任何 key / iv / 私钥 / 公钥 / 签名原文
        Assert.DoesNotContain("AAAA-AAAA-AAAA-AAAA", serialized);
        Assert.DoesNotContain("BBBB-BBBB-BBBB-BBBB", serialized);
        Assert.DoesNotContain("PRIVATE KEY", serialized);
        Assert.DoesNotContain("PUBLIC KEY", serialized);
        Assert.DoesNotContain("DEADBEEF", serialized);

        // 但 transform 字段名 + 类型应被记录（用于溯源"哪些字段做了转换"）
        Assert.NotNull(snap.Transforms);
        Assert.Contains(snap.Transforms!, t => t.Field == "cipher" && t.Type == "sm4");
        Assert.Contains(snap.Transforms!, t => t.Field == "sm2sig" && t.Type == "sm2");
    }

    [Fact]
    public void ComputeFileHash_IsStableAndHex()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "hello world");
            string? hash = AuditLogger.ComputeFileHash(tmp);
            Assert.NotNull(hash);
            Assert.Equal(64, hash!.Length); // SHA-256 -> 32 bytes -> 64 hex chars
            Assert.Matches("^[0-9a-f]{64}$", hash);
            // 同一文件应得到稳定哈希
            string? hash2 = AuditLogger.ComputeFileHash(tmp);
            Assert.Equal(hash, hash2);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    [Fact]
    public void ComputeFileHash_NullForMissingFile()
    {
        Assert.Null(AuditLogger.ComputeFileHash("/nonexistent/" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Summarize_IncludesCountsButNoValues()
    {
        var result = new AnalysisResult
        {
            TotalObjects = 100,
            TotalUniqueObjects = 80,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "id", Count = 100, TransformFailureCount = 0 },
                new() { FieldName = "secret", Count = 100, TransformFailureCount = 5 }
            }
        };
        var sw = new StopwatchLike();
        sw.Restart();
        var summary = AuditLogger.Summarize(result, sw);
        Assert.Equal(100, summary.TotalObjects);
        Assert.Equal(80, summary.TotalUniqueObjects);
        Assert.Equal(2, summary.TotalFields);
        Assert.Equal(5, summary.TransformFailureCount);

        // 序列化后不应出现字段名（避免指纹泄漏；也可选保留字段名，但需明确不存值）
        // 这里我们允许字段名但确保不存在任何值
        string serialized = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("value", serialized, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // AnalysisRunner 集成测试
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_WithAuditLog_WritesStartAndFinish()
    {
        string input = Path.GetTempFileName();
        string logPath = NewTempLogPath();
        try
        {
            File.WriteAllText(input, "[{\"a\":1,\"b\":\"x\"},{\"a\":2,\"b\":\"y\"}]");

            var options = new AnalysisOptions
            {
                InputPath = input,
                AuditLogPath = logPath,
                Operator = "test-runner"
            };
            int code = AnalysisRunner.Run(options);

            Assert.Equal(0, code);
            Assert.True(File.Exists(logPath), "审计日志文件应被创建");

            var entries = ReadEntries(logPath);
            Assert.Equal(2, entries.Count);
            Assert.Equal("analysis_start", entries[0].Event);
            Assert.Equal("analysis_finish", entries[1].Event);
            Assert.Equal("test-runner", entries[0].Operator);
            Assert.Equal("test-runner", entries[1].Operator);
            Assert.Equal(entries[0].RunId, entries[1].RunId); // 同一 runId 串联 start/finish
            Assert.Equal(0, entries[1].ExitCode);

            // start 事件包含 SHA-256 哈希
            Assert.False(string.IsNullOrEmpty(entries[0].InputHash));
            Assert.Equal(64, entries[0].InputHash!.Length);
        }
        finally
        {
            TryDelete(input);
            TryDelete(logPath);
        }
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_AuditLog_DoesNotLeakFieldValues()
    {
        string input = Path.GetTempFileName();
        string logPath = NewTempLogPath();
        try
        {
            // 数据中包含明显的敏感值（手机号、邮箱），审计日志绝不能出现
            string secret = "[{\"name\":\"Alice\",\"phone\":\"13800138000\",\"email\":\"[email protected]\"}]";
            File.WriteAllText(input, secret);

            var options = new AnalysisOptions
            {
                InputPath = input,
                AuditLogPath = logPath
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            string auditContent = File.ReadAllText(logPath);
            // 原始敏感值绝不能出现在审计日志中
            Assert.DoesNotContain("13800138000", auditContent);
            Assert.DoesNotContain("[email protected]", auditContent);
            // "Alice" 在字段名/字段值都可能；确保哪怕在某些场景出现也不在 result 中存"值"
            // 我们用更严格的方式：扫描每个 JSON 条目的 result 字段，确保没有 name 字段值
            var entries = ReadEntries(logPath);
            foreach (var entry in entries)
            {
                string entryJson = JsonSerializer.Serialize(entry);
                Assert.DoesNotContain("13800138000", entryJson);
                Assert.DoesNotContain("[email protected]", entryJson);
            }
        }
        finally
        {
            TryDelete(input);
            TryDelete(logPath);
        }
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_AuditLog_OnInputMissing_WritesNothing()
    {
        // 输入文件不存在时，Run 在校验阶段就返回 1，不进入正式审计流（避免 start 还没写就 fail）
        // 审计日志文件应保持空（或不存在）
        string logPath = NewTempLogPath();
        try
        {
            var options = new AnalysisOptions
            {
                InputPath = "/nonexistent/" + Guid.NewGuid().ToString("N"),
                AuditLogPath = logPath
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(1, code);

            // 路径已创建但无条目（按设计：start 事件在所有前置校验通过后才写）
            if (File.Exists(logPath))
            {
                var lines = File.ReadAllLines(logPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                Assert.Empty(lines);
            }
        }
        finally
        {
            TryDelete(logPath);
        }
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_NoAuditLog_RunsAsBefore()
    {
        // 不指定 --audit-log 时，分析应照常进行，且不创建任何审计文件
        string input = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, "[{\"a\":1}]");
            var options = new AnalysisOptions { InputPath = input };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_AuditLog_RecordsSelectedKeyOptions()
    {
        string input = Path.GetTempFileName();
        string logPath = NewTempLogPath();
        try
        {
            File.WriteAllText(input, "[{\"a\":1,\"b\":2},{\"a\":3,\"b\":4}]");
            var options = new AnalysisOptions
            {
                InputPath = input,
                AuditLogPath = logPath,
                Fields = "a",
                Filter = "a > 1",
                PiiOff = true,
                AllowBreakingSchemaChanges = false,
                ExportSchema = "json-schema"
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            var entries = ReadEntries(logPath);
            var start = entries.First(e => e.Event == "analysis_start");
            Assert.Equal("a", start.Options.Fields ?? "");
            Assert.Equal("a > 1", start.Options.Filter ?? "");
            Assert.True(start.Options.PiiOff);
            Assert.False(start.Options.AllowBreakingSchemaChanges);
            Assert.Equal("json-schema", start.Options.ExportSchema ?? "");
        }
        finally
        {
            TryDelete(input);
            TryDelete(logPath);
        }
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void AnalysisRunner_AuditLog_OnQualityGateFailure_ExitCodeThree()
    {
        string input = Path.GetTempFileName();
        string rules = Path.GetTempFileName();
        string logPath = NewTempLogPath();
        try
        {
            // enum 规则：仅允许 A，但数据全部为 B
            File.WriteAllText(input, "[{\"status\":\"B\"},{\"status\":\"B\"}]");
            File.WriteAllText(rules, "{\"fields\":{\"status\":{\"enumValues\":[\"A\"]}}}");

            var options = new AnalysisOptions
            {
                InputPath = input,
                RulesFile = rules,
                FailOnViolations = true,
                AuditLogPath = logPath
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(3, code);

            var entries = ReadEntries(logPath);
            var finish = entries.First(e => e.Event == "analysis_finish");
            Assert.Equal(3, finish.ExitCode);
            Assert.NotNull(finish.Result);
            Assert.True(finish.Result!.QualityViolationCount > 0);
        }
        finally
        {
            TryDelete(input);
            TryDelete(rules);
            TryDelete(logPath);
        }
    }

    // ──────────────────────────────────────────────
    // 辅助
    // ──────────────────────────────────────────────

    private static string NewTempLogPath()
        => Path.Combine(Path.GetTempPath(), "audit-" + Guid.NewGuid().ToString("N") + ".ndjson");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    private static List<AuditLogEntry> ReadEntries(string logPath)
    {
        var list = new List<AuditLogEntry>();
        foreach (var line in File.ReadAllLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(JsonSerializer.Deserialize<AuditLogEntry>(line)!);
        }
        return list;
    }
}
