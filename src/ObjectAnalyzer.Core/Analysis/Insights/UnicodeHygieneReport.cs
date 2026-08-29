using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Unicode 卫生检查结果（中文场景极常见的脏数据：全/半角混用、不可见字符、控制字符）。
/// 字段级统计在分析阶段流式累加，单条值不落盘。
/// </summary>
public sealed class UnicodeHygieneReport
{
    /// <summary>被检查的字符串样本数（流式累加，不重复计空值）。</summary>
    [JsonPropertyName("totalSamples")]
    public long TotalSamples { get; set; }

    /// <summary>全角字符出现次数（FF01-FF5E / 3000-303F / FF00 等全角区段）。</summary>
    [JsonPropertyName("fullwidthCount")]
    public long FullwidthCount { get; set; }

    /// <summary>半角字符出现次数（基本 ASCII 可打印区段）。</summary>
    [JsonPropertyName("halfwidthCount")]
    public long HalfwidthCount { get; set; }

    /// <summary>不可见字符（除普通空白 / 换行）出现次数，如 ZWSP/U+200B、BOM/U+FEFF、零宽连接符 U+200D 等。</summary>
    [JsonPropertyName("invisibleCount")]
    public long InvisibleCount { get; set; }

    /// <summary>控制字符（除 \t \r \n）出现次数，如 \0、\b、ESC、删除符 DEL/U+007F 等。</summary>
    [JsonPropertyName("controlCharCount")]
    public long ControlCharCount { get; set; }

    /// <summary>BOM / 零宽空格等仅"无内容但占字符位置"字符出现次数。</summary>
    [JsonPropertyName("zeroWidthCount")]
    public long ZeroWidthCount { get; set; }

    /// <summary>双向控制符出现次数（LRE/RLE/PDF/LRO/RLO 等，可能导致文本渲染歧义）。</summary>
    [JsonPropertyName("bidiControlCount")]
    public long BidiControlCount { get; set; }

    /// <summary>前后空白出现次数（首尾含空白字符，可能导致"看似相同实则不同"）。</summary>
    [JsonPropertyName("surroundingWhitespaceCount")]
    public long SurroundingWhitespaceCount { get; set; }

    /// <summary>大小写混用样本数（同一字符串内同时含 a-z 与 A-Z；典型场景：复制粘贴时大小写不一致）。</summary>
    [JsonPropertyName("mixedCaseCount")]
    public long MixedCaseCount { get; set; }

    /// <summary>大小写不一致率（混合样本 / 全部），0.0~1.0。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MixedCaseRatio => TotalSamples > 0 ? (double)MixedCaseCount / TotalSamples : null;

    /// <summary>全角占比（全角字符 / （全角 + 半角）），0.0~1.0；分母为 0 时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FullwidthRatio
    {
        get
        {
            long denom = FullwidthCount + HalfwidthCount;
            return denom > 0 ? (double)FullwidthCount / denom : null;
        }
    }

    /// <summary>卫生评分（0-100；越高越干净）。综合全角混用、不可见字符、控制字符、大小写混用。100 = 完美。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HygieneScore { get; set; }
}
