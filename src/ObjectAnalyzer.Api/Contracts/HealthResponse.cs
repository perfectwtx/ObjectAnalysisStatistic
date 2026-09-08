namespace ObjectAnalyzer.Api;

/// <summary>GET /api/health 响应。</summary>
public sealed record HealthResponse(string Status, string? Version)
{
    /// <summary>服务状态，固定为 "ok"。</summary>
    public string Status { get; init; } = Status;

    /// <summary>核心分析库的程序集版本号。</summary>
    public string? Version { get; init; } = Version;
}
