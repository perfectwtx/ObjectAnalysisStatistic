namespace ObjectAnalyzer.Tests;

public sealed class RegistryIdFormatTests
{
    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public void GuidAliases_ShareRegistrationOutputProgressAndRemoval(string format)
    {
        var registry = new AnalysisJobRegistry();
        var guid = Guid.NewGuid();
        var alias = guid.ToString(format).ToUpperInvariant();
        var canonical = guid.ToString("N");
        using var cts = new CancellationTokenSource();
        using var duplicate = new CancellationTokenSource();
        var meta = new AnalysisJobMeta("test", "json", null, DateTimeOffset.UtcNow);
        registry.Register(alias, cts, meta);
        Assert.Equal(canonical, registry.Get(canonical)!.Id);
        Assert.Throws<InvalidOperationException>(() => registry.Register(canonical, duplicate, meta));
        registry.ReportProgress(alias, 42);
        Assert.Equal(42, registry.Get(canonical)!.ProcessedObjects);
        var result = new AnalysisResult();
        var preview = new[] { new Dictionary<string, object?> { ["marker"] = "value" } };
        var policy = PiiPolicy.Resolve(result);
        registry.StageOutput(alias, preview, false, policy);
        Assert.Same(preview, registry.Get(canonical)!.Preview);
        registry.Complete(alias, result, preview, false, policy);
        Assert.Equal(AnalysisStatus.Completed, registry.Get(canonical)!.Status);
        Assert.Same(result, registry.Get(alias)!.Result);
        Assert.True(registry.Remove(alias));
        Assert.Null(registry.Get(canonical));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("X")]
    public void GuidAliases_CancelAndFailTheCanonicalJob(string format)
    {
        var registry = new AnalysisJobRegistry();
        var guid = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        registry.Register(guid.ToString("N"), cts, new AnalysisJobMeta("test", "json", null, DateTimeOffset.UtcNow));
        Assert.True(registry.Cancel(guid.ToString(format).ToUpperInvariant()));
        Assert.True(cts.IsCancellationRequested);
        registry.Remove(guid.ToString("N"));

        using var failedCts = new CancellationTokenSource();
        registry.Register(guid.ToString("N"), failedCts, new AnalysisJobMeta("test", "json", null, DateTimeOffset.UtcNow));
        registry.Fail(guid.ToString(format), new InvalidOperationException("test"));
        Assert.Equal(AnalysisStatus.Failed, registry.Get(guid.ToString("N"))!.Status);
        registry.Remove(guid.ToString("N"));
    }

    [Fact]
    public void NonGuidIdentifiers_RetainTheirCaseSensitiveIdentity()
    {
        var registry = new AnalysisJobRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register("legacy-ID", cts, new AnalysisJobMeta("test", "json", null, DateTimeOffset.UtcNow));
        Assert.NotNull(registry.Get("legacy-ID"));
        Assert.Null(registry.Get("legacy-id"));
        registry.Remove("legacy-ID");
    }
}
