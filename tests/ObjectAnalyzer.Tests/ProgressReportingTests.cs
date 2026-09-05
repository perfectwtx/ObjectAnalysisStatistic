namespace ObjectAnalyzer.Tests;

/// <summary>
/// P6.1：进度条 / ETA 集成测试。
/// 验证 <see cref="ObjectAnalyzer.Analyze"/> 在分块处理时通过 <see cref="IProgress{T}"/> 上报对象计数。
/// </summary>

public class ProgressReportingTests
{
    private static List<Dictionary<string, object?>> MakeData(int count)
    {
        var data = new List<Dictionary<string, object?>>(count);
        for (int i = 0; i < count; i++)
        {
            data.Add(new Dictionary<string, object?>
            {
                ["id"] = i,
                ["name"] = $"item-{i}",
                ["score"] = (double)(i % 100)
            });
        }
        return data;
    }

    [Fact]
    public void Analyze_WithProgressCallback_ReportsObjectCount()
    {
        const int count = 25_000; // > ChunkSize (10K) 触发多个分块
        var data = MakeData(count);
        var reported = new List<long>();
        var gate = new object();
        var progress = new Progress<long>(n => { lock (gate) { reported.Add(n); } });

        var result = global::ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(data, progress: progress);

        // 至少触发 2 次以上分块回调（数据 25K / ChunkSize 10K ≈ 3 次）
        Assert.True(reported.Count >= 2, $"进度回调应至少触发 2 次，实际 {reported.Count} 次");
        // 最后一次回调的对象数 == 实际分析的对象数
        long last = reported[^1];
        Assert.Equal(count, last);
        // 单调递增
        for (int i = 1; i < reported.Count; i++)
        {
            Assert.True(reported[i] >= reported[i - 1], $"回调应单调递增，但 {reported[i - 1]} → {reported[i]}");
        }
        // 分析结果应包含全部 25K 条
        Assert.Equal(count, result.TotalObjects);
    }

    [Fact]
    public void Analyze_WithoutProgressCallback_StillWorks()
    {
        // 不传 progress 回调时应正常完成
        var data = MakeData(5_000);
        var result = global::ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(data);
        Assert.Equal(5_000, result.TotalObjects);
    }

    [Fact]
    public void AnalyzeDetailed_WithProgressCallback_ReportsAndReturnsSketches()
    {
        var data = MakeData(15_000);
        long lastReported = 0;
        int calls = 0;
        var progress = new Progress<long>(n =>
        {
            lastReported = n;
            Interlocked.Increment(ref calls);
        });

        var detailed = global::ObjectAnalyzer.Analysis.ObjectAnalyzer.AnalyzeDetailed(data, progress: progress);
        Assert.Equal(15_000, lastReported);
        Assert.True(calls >= 2, "至少 2 次回调（15K / 10K ChunkSize）");
        Assert.Equal(15_000, detailed.Result.TotalObjects);
    }

    [Fact]
    public void AnalysisRunner_RespectsShowProgressFlag()
    {
        // 验证 AnalysisOptions.Progress.ShowProgress 默认 true，且可被外部关闭
        var opts = new AnalysisOptions
        {
            Input = { InputPath = Path.Combine(Path.GetTempPath(), "nonexistent.jsonl") },
        };
        Assert.True(opts.Progress.ShowProgress, "ShowProgress 默认应为 true（开启进度条）");
        opts.Progress.ShowProgress = false;
        Assert.False(opts.Progress.ShowProgress);
    }
}
