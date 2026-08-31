using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 采样策略：蓄水池随机抽样 + 等距系统抽样，两种策略互斥（同时启用时优先 reservoir）。
/// </summary>
public class SamplingConfig
{
    /// <summary>蓄水池随机抽样容量。&gt; 0 启用。与 <c>--sample-reservoir</c> 对应。</summary>
    [JsonPropertyName("reservoir")]
    public int? Reservoir { get; set; }

    /// <summary>等距系统抽样步长。&gt; 1 启用（每 N 个对象取 1 个）。与 <c>--sample-step</c> 对应。</summary>
    [JsonPropertyName("step")]
    public int? Step { get; set; }
}
