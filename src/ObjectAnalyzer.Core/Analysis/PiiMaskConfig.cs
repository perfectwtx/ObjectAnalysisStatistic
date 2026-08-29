using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 自定义打码格式：保留前缀/后缀字符，其余以 maskChar 打码；或整值打码。
/// 规则：优先 maskAll（整值打码）；否则按 keepPrefix/keepSuffix 保留前后缀，中间用 maskChar 填充。
/// </summary>
public class PiiMaskConfig
{
    /// <summary>保留前缀字符数（默认 0）。</summary>
    [JsonPropertyName("keepPrefix")]
    public int KeepPrefix { get; set; }

    /// <summary>保留后缀字符数（默认 0）。</summary>
    [JsonPropertyName("keepSuffix")]
    public int KeepSuffix { get; set; }

    /// <summary>打码字符（默认 *）。</summary>
    [JsonPropertyName("maskChar")]
    public string MaskChar { get; set; } = "*";

    /// <summary>是否整值打码（忽略 keepPrefix/keepSuffix，整个值替换为 maskChar）。</summary>
    [JsonPropertyName("maskAll")]
    public bool MaskAll { get; set; }
}
