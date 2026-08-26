using System;
using System.Collections.Generic;
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

/// <summary>
/// Unicode 卫生检查器：流式 / 单值两种调用方式。
/// "脏数据"指对下游解析（数据库写入、JSON 反序列化、用户展示）有潜在干扰的字符与形态。
/// </summary>
public static class UnicodeHygieneChecker
{
    private const char ZWSP = '\u200B';           // 零宽空格
    private const char ZWNJ = '\u200C';           // 零宽非连接符
    private const char ZWJ = '\u200D';            // 零宽连接符
    private const char BOM = '\uFEFF';            // 字节序标记
    private const char WORD_JOINER = '\u2060';    // 词结合符
    private const char SOFT_HYPHEN = '\u00AD';    // 软连字符（视觉不可见）
    private const char COMBINING_GRAPHEME_JOINER = '\u034F'; // 组合附加符号连接符

    /// <summary>
    /// 检查单个字符串的卫生情况。返回每类字符的出现次数（不返回具体位置）。
    /// </summary>
    public static UnicodeHygieneReport Check(string? value)
    {
        var report = new UnicodeHygieneReport();
        if (string.IsNullOrEmpty(value)) return report;

        report.TotalSamples = 1;

        int lettersLower = 0, lettersUpper = 0;
        bool hasLeadingSpace = false, hasTrailingSpace = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            int code = c;

            // 普通空白
            if (char.IsWhiteSpace(c))
            {
                if (i == 0) hasLeadingSpace = true;
                if (i == value.Length - 1) hasTrailingSpace = true;
                continue;
            }

            // 控制字符（除 \t \n \r \v \f）
            if (char.IsControl(c) && c != '\t' && c != '\n' && c != '\r' && c != '\v' && c != '\f')
            {
                report.ControlCharCount++;
                continue;
            }

            // 零宽 / 不可见 / 私用
            if (c == ZWSP || c == ZWNJ || c == ZWJ || c == BOM || c == WORD_JOINER
                || c == SOFT_HYPHEN || c == COMBINING_GRAPHEME_JOINER
                || (code >= 0xE000 && code <= 0xF8FF)  // 私用平面
                || (code >= 0xF0000 && code <= 0xFFFFD) // 增补私用平面 A
                || (code >= 0x100000 && code <= 0x10FFFD)) // 增补私用平面 B
            {
                report.InvisibleCount++;
                if (c == ZWSP || c == ZWNJ || c == ZWJ || c == BOM || c == WORD_JOINER
                    || c == SOFT_HYPHEN || c == COMBINING_GRAPHEME_JOINER)
                {
                    report.ZeroWidthCount++;
                }
                continue;
            }

            // 双向控制符（LRE/RLE/PDF/LRO/RLO/LRI/RLI/FSI/PDI）
            if (code is >= 0x202A and <= 0x202E
                || code is >= 0x2066 and <= 0x2069)
            {
                report.BidiControlCount++;
                continue;
            }

            // ASCII 半角字母数字
            if (code >= 0x21 && code <= 0x7E)
            {
                report.HalfwidthCount++;
                if (c >= 'a' && c <= 'z') lettersLower++;
                if (c >= 'A' && c <= 'Z') lettersUpper++;
                continue;
            }

            // 全角 ASCII（FF01-FF5E 对应 21-7E 的全角形态）
            if (code >= 0xFF01 && code <= 0xFF5E)
            {
                report.FullwidthCount++;
                // 全角字母不算 a-z / A-Z（视为已归类为全角）
                continue;
            }

            // 中文标点（CJK 符号和标点区段 3000-303F）
            if (code >= 0x3000 && code <= 0x303F)
            {
                report.FullwidthCount++;
                continue;
            }

            // 全角空格 U+3000
            if (code == 0x3000)
            {
                report.FullwidthCount++;
                continue;
            }

            // 其他字符（中日韩表意、扩展区段等）不计入全/半角比例
        }

        if (hasLeadingSpace || hasTrailingSpace)
        {
            report.SurroundingWhitespaceCount = 1;
        }

        // 大小写混用：同一字符串内既有 a-z 又有 A-Z 视为混用
        if (lettersLower > 0 && lettersUpper > 0)
        {
            report.MixedCaseCount = 1;
        }

        report.HygieneScore = ComputeScore(report);
        return report;
    }

    /// <summary>
    /// 合并另一份 <see cref="UnicodeHygieneReport"/>（线程局部实例汇总时使用）。
    /// </summary>
    public static void MergeInto(UnicodeHygieneReport target, UnicodeHygieneReport other)
    {
        target.TotalSamples += other.TotalSamples;
        target.FullwidthCount += other.FullwidthCount;
        target.HalfwidthCount += other.HalfwidthCount;
        target.InvisibleCount += other.InvisibleCount;
        target.ControlCharCount += other.ControlCharCount;
        target.ZeroWidthCount += other.ZeroWidthCount;
        target.BidiControlCount += other.BidiControlCount;
        target.SurroundingWhitespaceCount += other.SurroundingWhitespaceCount;
        target.MixedCaseCount += other.MixedCaseCount;
        target.HygieneScore = ComputeScore(target);
    }

    /// <summary>
    /// 卫生评分：100 减各类污染占比×权重，再钳制到 [0, 100]。
    /// 权重：控制字符 / 双向控制符 / 零宽 = 20 / 15 / 10；不可见 = 8；全角混用 &gt; 50% 时 5；前后空白 3；大小写混用 2。
    /// </summary>
    private static double ComputeScore(UnicodeHygieneReport r)
    {
        if (r.TotalSamples == 0) return 100.0;

        double penalty = 0;
        long chars = Math.Max(r.FullwidthCount + r.HalfwidthCount, 1);
        penalty += Math.Min(20.0, r.ControlCharCount * 5.0);
        penalty += Math.Min(15.0, r.BidiControlCount * 8.0);
        penalty += Math.Min(10.0, r.ZeroWidthCount * 4.0);
        penalty += Math.Min(8.0, r.InvisibleCount * 3.0);
        penalty += r.FullwidthRatio is > 0.5 and < 0.95 ? 5 : 0;
        penalty += r.MixedCaseRatio is > 0.05 and < 0.95 ? 2 : 0;
        penalty += r.SurroundingWhitespaceCount > 0 ? 3 : 0;

        return Math.Max(0.0, Math.Min(100.0, 100.0 - penalty));
    }
}
