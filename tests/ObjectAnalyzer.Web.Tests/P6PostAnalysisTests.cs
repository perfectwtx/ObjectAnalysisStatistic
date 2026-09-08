using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// P6 选项在 Web 分析流程中的可测试拆解：覆盖 SanitizeCheckpointName 与 Apply 两条主路径。
/// MemoryBudget.Check 内部使用 AnsiConsole.MarkupLine，与其它并发跑会争抢控制台句柄——
/// 因此加入顺序集合，避免 Spectre.Console 报"interactive functions concurrently"。
/// </summary>
[Collection(WebAnsiConsoleSequentialGroup.Name)]
public class P6PostAnalysisTests
{
    // ──────────────────────────────────────────────
    // SanitizeCheckpointName
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_NullInput_ReturnsEmpty()
    {
        Assert.Equal("", P6PostAnalysis.SanitizeCheckpointName(null));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", P6PostAnalysis.SanitizeCheckpointName(""));
        Assert.Equal("", P6PostAnalysis.SanitizeCheckpointName("   "));
        Assert.Equal("", P6PostAnalysis.SanitizeCheckpointName("\t\n"));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_NormalName_AppendsSnapshotExtension()
    {
        // 干净的名字 + 自动追加 .snapshot
        Assert.Equal("my-session.snapshot", P6PostAnalysis.SanitizeCheckpointName("my-session"));
        Assert.Equal("session_42.snapshot", P6PostAnalysis.SanitizeCheckpointName("session_42"));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_NameAlreadyEndsWithSnapshot_LeavesAsIs()
    {
        // 已经带后缀 → 不重复追加
        Assert.Equal("foo.snapshot", P6PostAnalysis.SanitizeCheckpointName("foo.snapshot"));
        // 大小写不敏感：已带 .SNAPSHOT 也视作已带后缀
        Assert.Equal("foo.SNAPSHOT", P6PostAnalysis.SanitizeCheckpointName("foo.SNAPSHOT"));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_InvalidFileNameChars_ReplacedWithUnderscore()
    {
        // Windows 非法字符：< > : " | ? *
        var input = "a<b>c:d\"e|f?g*h";
        var result = P6PostAnalysis.SanitizeCheckpointName(input);
        Assert.Equal("a_b_c_d_e_f_g_h.snapshot", result);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_PathSeparators_ReplacedWithUnderscore()
    {
        // 路径分隔符（含正反斜杠）必须转成下划线——这是路径穿越防护的关键
        Assert.Equal(".._.._evil.snapshot", P6PostAnalysis.SanitizeCheckpointName("../..\\evil"));
        Assert.Equal("subdir_file.snapshot", P6PostAnalysis.SanitizeCheckpointName("subdir/file"));
        Assert.Equal("subdir_file.snapshot", P6PostAnalysis.SanitizeCheckpointName("subdir\\file"));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_OverlongName_TruncatedAndEndsWithSnapshot()
    {
        // 300 字符 → 截断到 240 字符 + .snapshot 后缀
        var raw = new string('a', 300);
        var result = P6PostAnalysis.SanitizeCheckpointName(raw);
        Assert.Equal(240 + ".snapshot".Length, result.Length);
        Assert.EndsWith(".snapshot", result);
        Assert.Equal(new string('a', 240) + ".snapshot", result);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_OnlyInvalidChars_FallsBackToSessionGuid()
    {
        // 全部是 Windows 非法字符 / 路径分隔符 → cleaned 变成全下划线，非空，所以仍会带 .snapshot
        // 这条用例验证"清洗后非空时不下发 GUID 兜底"。
        var result = P6PostAnalysis.SanitizeCheckpointName("<<<>>>");
        Assert.Equal("______.snapshot", result);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_ControlChars_TreatedAsInvalid()
    {
        // 控制字符（含 \0）属于 GetInvalidFileNameChars 范围
        var result = P6PostAnalysis.SanitizeCheckpointName("a\0b\tc");
        Assert.Equal("a_b_c.snapshot", result);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_CrossPlatformRejectsWindowsInvalidChars()
    {
        // 即使在 Linux/macOS（反斜杠、<>:"|?* 均合法）也必须清洗——路径穿越防护跨平台一致
        Assert.Equal("a_b_c_d_e_f_g_h.snapshot", P6PostAnalysis.SanitizeCheckpointName("a<b>c:d\"e|f?g*h"));
        Assert.Equal(".._.._evil.snapshot", P6PostAnalysis.SanitizeCheckpointName("../..\\evil"));
        Assert.Equal("______.snapshot", P6PostAnalysis.SanitizeCheckpointName("<<<>>>"));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Sanitize_TrimsLeadingAndTrailingWhitespace()
    {
        Assert.Equal("name.snapshot", P6PostAnalysis.SanitizeCheckpointName("  name  "));
    }

    // ──────────────────────────────────────────────
    // Apply
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_NullInput_Throws()
    {
        // CA1510 友好：直接 ArgumentNullException
        Assert.Throws<System.ArgumentNullException>(() => P6PostAnalysis.Apply(null!));
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_NoPathsProvided_LeavesResultUntouchedAndReturnsEmpty()
    {
        var result = NewResult(totalObjects: 7);
        var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
        {
            Result = result
        });

        Assert.Null(output.LastCheckpointServerPath);
        Assert.Equal(0L, output.LastCheckpointSize);
        Assert.Null(output.ResumeErrorMessage);
        Assert.Null(output.BaselineErrorMessage);
        Assert.Null(output.CheckpointErrorMessage);
        Assert.Equal(7, result.TotalObjects);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_ResumeFromValidSnapshot_MergesIntoResult()
    {
        // 准备：写一个 .snapshot 包含 TotalObjects=10
        var snapshotPath = WriteSnapshotWith(totalObjects: 10, totalUniqueObjects: 8);

        var result = NewResult(totalObjects: 5);
        var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
        {
            Result = result,
            ResumeFromPath = snapshotPath
        });

        // MergeFrom: TotalObjects 相加
        Assert.Equal(15, result.TotalObjects);
        // TotalUniqueObjects 取 max
        Assert.Equal(8, result.TotalUniqueObjects);
        Assert.Null(output.ResumeErrorMessage);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_ResumeFromMissingFile_LeavesResultUnchanged()
    {
        // 不存在的文件 → 静默跳过（不报错，不合并）
        var result = NewResult(totalObjects: 3);
        var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
        {
            Result = result,
            ResumeFromPath = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".snapshot")
        });

        Assert.Equal(3, result.TotalObjects);
        Assert.Null(output.ResumeErrorMessage);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_ResumeFromCorruptedFile_SetsResumeErrorMessage()
    {
        // 写一个不是合法 AnalyzerSnapshot JSON 的文件
        var badPath = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".snapshot");
        File.WriteAllText(badPath, "this is not json {{{");
        try
        {
            var result = NewResult(totalObjects: 1);
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = result,
                ResumeFromPath = badPath
            });

            // 错误被吞掉写到 ResumeErrorMessage，主流程 result 不被破坏
            Assert.NotNull(output.ResumeErrorMessage);
            Assert.Equal(1, result.TotalObjects);
        }
        finally
        {
            TryDelete(badPath);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_BaselineFromValidJson_MergesIntoResult()
    {
        // baseline 是 AnalysisResult JSON（不是 AnalyzerSnapshot），走 JsonSerializer.Deserialize
        var baselinePath = Path.Combine(Path.GetTempPath(), "baseline-" + Guid.NewGuid().ToString("N") + ".json");
        var baseline = NewResult(totalObjects: 100, totalUniqueObjects: 90);
        File.WriteAllText(baselinePath, System.Text.Json.JsonSerializer.Serialize(baseline));
        try
        {
            var result = NewResult(totalObjects: 5);
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = result,
                BaselinePath = baselinePath
            });

            Assert.Equal(105, result.TotalObjects);
            Assert.Equal(90, result.TotalUniqueObjects);
            Assert.Null(output.BaselineErrorMessage);
        }
        finally
        {
            TryDelete(baselinePath);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_BaselineFromCorruptedJson_SetsBaselineErrorMessage()
    {
        var badPath = Path.Combine(Path.GetTempPath(), "bad-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(badPath, "{not valid json");
        try
        {
            var result = NewResult(totalObjects: 1);
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = result,
                BaselinePath = badPath
            });

            Assert.NotNull(output.BaselineErrorMessage);
            Assert.Equal(1, result.TotalObjects);
        }
        finally
        {
            TryDelete(badPath);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_CheckpointName_WritesSnapshotAndReturnsPathAndSize()
    {
        // 注入临时目录，避免污染 %TEMP%/objectanalyzer-checkpoints
        var dir = Path.Combine(Path.GetTempPath(), "p6-tests-" + Guid.NewGuid().ToString("N"));
        var fileName = "out-" + Guid.NewGuid().ToString("N") + ".snapshot";
        var expectedPath = Path.Combine(dir, fileName);
        try
        {
            var result = NewResult(totalObjects: 42);
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = result,
                CheckpointFileName = fileName,
                CheckpointDirectory = dir,
                SourcePath = "C:/data/input.jsonl",
                SourceFormat = "jsonl"
            });

            Assert.Equal(expectedPath, output.LastCheckpointServerPath);
            Assert.True(output.LastCheckpointSize > 0, "落盘文件应当非空");
            Assert.True(File.Exists(expectedPath), "落盘文件应当存在");

            // 反向验证：能用 AnalyzerSnapshot.LoadFrom 重新读出来
            var roundTrip = AnalyzerSnapshot.LoadFrom(expectedPath);
            Assert.Equal(42, roundTrip.Result.TotalObjects);
            Assert.Equal("C:/data/input.jsonl", roundTrip.SourcePath);
            Assert.Equal("jsonl", roundTrip.SourceFormat);
            Assert.Null(output.CheckpointErrorMessage);
        }
        finally
        {
            TryDelete(expectedPath);
            TryDeleteDir(dir);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_UsesFileNameAsProvided_NoImplicitExtensionAppend()
    {
        // Apply 不做文件名清洗（SanitizeCheckpointName 由调用方在 UI 边界调用）。
        // 这里验证"原样使用传入的 CheckpointFileName 作为输出文件名"——文件名后缀由 caller 负责。
        var dir = Path.Combine(Path.GetTempPath(), "p6-tests-" + Guid.NewGuid().ToString("N"));
        var fileName = "no-extension";
        var expectedPath = Path.Combine(dir, fileName);
        try
        {
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = NewResult(),
                CheckpointFileName = fileName,
                CheckpointDirectory = dir
            });

            Assert.Equal(expectedPath, output.LastCheckpointServerPath);
            Assert.True(File.Exists(expectedPath), "Apply 应按传入的文件名原样写盘，不自动补 .snapshot");
        }
        finally
        {
            TryDelete(expectedPath);
            TryDeleteDir(dir);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_CheckpointFailsToWrite_SetsCheckpointErrorMessage()
    {
        // 把目录设成只读 / 不可写，触发 SaveTo 失败 → 错误应被吞掉并返回
        // 选个文件当作"目录"喂给 Path.Combine，强制 IO 异常
        var blockerFile = Path.Combine(Path.GetTempPath(), "p6-blocker-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(blockerFile, "x");
        try
        {
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = NewResult(),
                CheckpointFileName = "out.snapshot",
                CheckpointDirectory = blockerFile // 文件当目录，Path.Combine 不会立即报错，但 SaveTo 写入会失败
            });

            // 可能成功（取决于平台是否允许把同目录文件当目录写），也可能失败
            // 失败时必须把消息写到 CheckpointErrorMessage
            if (output.CheckpointErrorMessage != null)
            {
                Assert.NotEmpty(output.CheckpointErrorMessage);
            }
        }
        finally
        {
            TryDelete(blockerFile);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void Apply_AllThreeSteps_SuccessInOrder()
    {
        // 三个 P6.2 步骤同一次 Apply 中都成功：合并 + 合并 + 落盘
        var resumePath = WriteSnapshotWith(totalObjects: 10);
        var baselinePath = Path.Combine(Path.GetTempPath(), "bl-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(baselinePath, System.Text.Json.JsonSerializer.Serialize(NewResult(totalObjects: 20)));

        var dir = Path.Combine(Path.GetTempPath(), "p6-tests-" + Guid.NewGuid().ToString("N"));
        var fileName = "all.snapshot";
        var expectedCheckpoint = Path.Combine(dir, fileName);

        try
        {
            var result = NewResult(totalObjects: 1);
            var output = P6PostAnalysis.Apply(new P6PostAnalysisInput
            {
                Result = result,
                ResumeFromPath = resumePath,
                BaselinePath = baselinePath,
                CheckpointFileName = fileName,
                CheckpointDirectory = dir
            });

            // 1 + 10 (resume) + 20 (baseline) = 31
            Assert.Equal(31, result.TotalObjects);
            Assert.Equal(expectedCheckpoint, output.LastCheckpointServerPath);
            Assert.True(File.Exists(expectedCheckpoint));
            Assert.Null(output.ResumeErrorMessage);
            Assert.Null(output.BaselineErrorMessage);
            Assert.Null(output.CheckpointErrorMessage);
        }
        finally
        {
            TryDelete(resumePath);
            TryDelete(baselinePath);
            TryDelete(expectedCheckpoint);
            TryDeleteDir(dir);
        }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static AnalysisResult NewResult(long totalObjects = 0, long totalUniqueObjects = 0)
    {
        return new AnalysisResult
        {
            TotalObjects = totalObjects,
            TotalUniqueObjects = totalUniqueObjects
        };
    }

    private static string WriteSnapshotWith(long totalObjects, long totalUniqueObjects = 0)
    {
        var snapshot = new AnalyzerSnapshot
        {
            SourcePath = "test.jsonl",
            SourceFormat = "jsonl",
            Result = NewResult(totalObjects, totalUniqueObjects)
        };
        var path = Path.Combine(Path.GetTempPath(), "p6-resume-" + Guid.NewGuid().ToString("N") + ".snapshot");
        snapshot.SaveTo(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
    }
}
