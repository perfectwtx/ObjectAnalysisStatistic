namespace ObjectAnalyzer.Tests;

/// <summary>
/// AnalysisJobRegistry 单元测试：覆盖"jobId 注册 / 取消 / 完成 / 失败 / 清理"全部分支。
/// 测试用 Guid.NewGuid() 注册独立作业，互不干扰；不用 Collection 串行化也能保证隔离。
/// </summary>
public class AnalysisJobRegistryTests
{
    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Register_ReturnsUniqueJobIds()
    {
        var registry = new AnalysisJobRegistry();
        var meta = MakeMeta();

        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();
        try
        {
            string id1 = registry.Register(cts1, meta);
            string id2 = registry.Register(cts2, meta);
            Assert.NotEqual(id1, id2);
            Assert.Equal(32, id1.Length); // Guid.NewGuid("N") 是 32 个 hex 字符（无连字符）
        }
        finally
        {
            cts1.Dispose();
            cts2.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Get_NonExistentJob_ReturnsNull()
    {
        var registry = new AnalysisJobRegistry();
        Assert.Null(registry.Get("nope"));
        Assert.Null(registry.Get(""));
        Assert.Null(registry.Get(null!));
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Get_ExistingJob_RunningSnapshot()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        try
        {
            var meta = MakeMeta("data.jsonl", "jsonl");
            string id = registry.Register(cts, meta);

            var snapshot = registry.Get(id);

            Assert.NotNull(snapshot);
            Assert.Equal(id, snapshot!.Id);
            Assert.Equal(AnalysisStatus.Running, snapshot.Status);
            Assert.Equal("data.jsonl", snapshot.Meta.DataFileName);
            Assert.Equal("jsonl", snapshot.Meta.Format);
            Assert.Null(snapshot.Result);
            Assert.Null(snapshot.Error);
            Assert.Null(snapshot.TerminalAt);
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Cancel_RunningJob_TriggersTokenAndUpdatesStatus()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        try
        {
            bool cancelled = false;
            cts.Token.Register(() => cancelled = true);

            var id = registry.Register(cts, MakeMeta());
            bool ok = registry.Cancel(id);

            Assert.True(ok);
            Assert.True(cancelled);

            var snap = registry.Get(id);
            Assert.Equal(AnalysisStatus.Cancelled, snap!.Status);
            Assert.NotNull(snap.TerminalAt);
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Cancel_TerminalJob_ReturnsFalse()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        try
        {
            var id = registry.Register(cts, MakeMeta());
            // 标记为已完成
            registry.Complete(id, new AnalysisResult { FieldStatistics = new List<FieldStatistic>() });

            bool ok = registry.Cancel(id);
            Assert.False(ok);
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Cancel_NonExistentJob_ReturnsFalse()
    {
        var registry = new AnalysisJobRegistry();
        Assert.False(registry.Cancel("missing"));
        Assert.False(registry.Cancel(""));
        Assert.False(registry.Cancel(null!));
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Complete_NullJobId_NoOp()
    {
        var registry = new AnalysisJobRegistry();
        // 不抛异常即通过
        registry.Complete("", new AnalysisResult());
        registry.Complete(null!, new AnalysisResult());
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Fail_SetsErrorAndStatus()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        try
        {
            var id = registry.Register(cts, MakeMeta());
            var err = new InvalidOperationException("boom");
            registry.Fail(id, err);

            var snap = registry.Get(id);
            Assert.Equal(AnalysisStatus.Failed, snap!.Status);
            Assert.Same(err, snap.Error);
            Assert.NotNull(snap.TerminalAt);
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Complete_TerminalJob_NoOp()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        try
        {
            var id = registry.Register(cts, MakeMeta());
            registry.Complete(id, new AnalysisResult());
            // 第二次 Complete 应当被忽略
            registry.Complete(id, new AnalysisResult());

            var snap = registry.Get(id);
            Assert.Equal(AnalysisStatus.Completed, snap!.Status);
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public async Task Cancel_AndComplete_Racing_LeavesOnlyOneTerminalState()
    {
        var registry = new AnalysisJobRegistry();
        using var cts = new CancellationTokenSource();
        var id = registry.Register(cts, MakeMeta());

        var cancel = Task.Run(() => registry.Cancel(id));
        var complete = Task.Run(() => registry.Complete(id, new AnalysisResult()));

        await Task.WhenAll(cancel, complete);

        var snapshot = registry.Get(id);
        Assert.NotNull(snapshot);
        Assert.True(
            snapshot!.Status is AnalysisStatus.Cancelled or AnalysisStatus.Completed);
        Assert.NotNull(snapshot.TerminalAt);
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void SweepExpired_RemovesTerminalJobs_KeepsRunning()
    {
        var registry = new AnalysisJobRegistry();
        var ctsRunning = new CancellationTokenSource();
        var ctsDone = new CancellationTokenSource();
        try
        {
            var runningId = registry.Register(ctsRunning, MakeMeta());
            var doneId = registry.Register(ctsDone, MakeMeta());
            registry.Complete(doneId, new AnalysisResult());

            // retention = 0 → 终态立即过期
            int removed = registry.SweepExpired(retention: TimeSpan.Zero);
            Assert.True(removed >= 1);
            Assert.NotNull(registry.Get(runningId));
            Assert.Null(registry.Get(doneId));
        }
        finally
        {
            ctsRunning.Dispose();
            ctsDone.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Cancel_AfterDisposedCts_DoesNotThrow()
    {
        var registry = new AnalysisJobRegistry();
        var cts = new CancellationTokenSource();
        var id = registry.Register(cts, MakeMeta());
        cts.Dispose();

        // 不应抛 ObjectDisposedException
        bool ok = registry.Cancel(id);
        Assert.False(ok);
    }

    [Fact]
    [Trait("Category", "JobRegistry")]
    public void Register_TriggersSweepExpired_OnNextRegister()
    {
        // 验证：Register 内部会清理过期的终态作业，避免无限增长
        var registry = new AnalysisJobRegistry();
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();
        var cts3 = new CancellationTokenSource();
        try
        {
            var done1 = registry.Register(cts1, MakeMeta());
            registry.Complete(done1, new AnalysisResult());

            // 这次 Register 会触发 SweepExpired，但因为 retention = 5min 不应清掉任何东西
            var newId = registry.Register(cts2, MakeMeta());
            Assert.NotNull(registry.Get(done1)); // 仍在（未过期）
            Assert.NotNull(registry.Get(newId));

            // 手动调用 SweepExpired(retention=0) 把 done1 清掉
            int removed = registry.SweepExpired(retention: TimeSpan.Zero);
            Assert.True(removed >= 1);
            Assert.Null(registry.Get(done1));
            Assert.NotNull(registry.Get(newId));

            // 第三次注册：仍能正常工作
            var anotherId = registry.Register(cts3, MakeMeta());
            Assert.NotNull(registry.Get(anotherId));
        }
        finally
        {
            cts1.Dispose();
            cts2.Dispose();
            cts3.Dispose();
        }
    }

    // ──────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────

    private static AnalysisJobMeta MakeMeta(string fileName = "data.jsonl", string format = "jsonl")
        => new(fileName, format, "test-operator", DateTimeOffset.UtcNow);
}
