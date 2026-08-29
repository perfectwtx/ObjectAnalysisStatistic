namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 简易秒表包装，便于在 Run 之外也能传入 AuditLogger（避免直接依赖 Stopwatch 增加耦合）。
/// </summary>
public sealed class StopwatchLike
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public long ElapsedMs => _sw.ElapsedMilliseconds;
    public void Restart() => _sw.Restart();
}
