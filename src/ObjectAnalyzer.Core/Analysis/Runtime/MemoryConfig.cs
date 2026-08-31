using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P6 内存预算监控：进程托管堆上限、超限行为模式。
/// </summary>
public class MemoryConfig
{
    /// <summary>进程托管堆上限（MB）。0 = 不监控。与 <c>--max-memory</c> 对应。</summary>
    [JsonPropertyName("maxMB")]
    public long? MaxMB { get; set; }

    /// <summary>超限行为模式：<c>soft</c>（自动降级到 50K 蓄水池采样 + 警告）/ <c>hard</c>（立即抛异常失败）。与 <c>--max-memory-mode</c> 对应。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}
