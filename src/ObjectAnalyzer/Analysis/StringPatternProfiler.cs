using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字符串模式自动归纳结果：对一组字符串归纳出形态模板（如 "###-####" / "1[3-9]########"），
/// 并报告各模板的覆盖率。典型应用：手机号、邮编、订单号、身份证等。
/// </summary>
public sealed class StringPatternProfile
{
    /// <summary>被分析样本数（流式收集，无重复去重）。</summary>
    [JsonPropertyName("totalSamples")]
    public long TotalSamples { get; set; }

    /// <summary>主导模板（覆盖样本数最多者）。无样本时为 null。</summary>
    [JsonPropertyName("dominantPattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringPatternTemplate? DominantPattern { get; set; }

    /// <summary>模板列表（按样本数降序；超过 TopN 上限仅保留前 N 个，其余合并到 "其他"）。</summary>
    [JsonPropertyName("patterns")]
    public List<StringPatternTemplate> Patterns { get; set; } = new();

    /// <summary>未被归纳到任何 Top 模板的样本数。TotalSamples − sum(Patterns.Count)。</summary>
    [JsonPropertyName("otherCount")]
    public long OtherCount { get; set; }
}

/// <summary>单个字符串模板及其覆盖统计。</summary>
public sealed class StringPatternTemplate
{
    /// <summary>模板字符串：# 表示数字、? 表示字母、* 表示字母数字、其余字面量保留。</summary>
    [JsonPropertyName("template")]
    public string Template { get; set; } = "";

    /// <summary>匹配该模板的样本数。</summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>覆盖率（count / total），0.0~1.0。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ratio { get; set; }

    /// <summary>匹配该模板的样例值（最多 3 个）。</summary>
    [JsonPropertyName("examples")]
    public List<string> Examples { get; set; } = new();

    public override string ToString() => $"{Template} ×{Count}";
}

/// <summary>
/// 字符串模式归纳器：流式 Add，最后 Build。
/// 规则：
///  - 数字字符（0-9）→ "#"
///  - 字母（a-z, A-Z）→ "?"
///  - 字母数字 → "*"
///  - 空白 / 标点 / 其他 → 字面保留（参与模板以兼容 "###-####" / "###.###.###" 这类带分隔符格式）
/// 模板归一化策略：相同模板合并计数；只保留非空样本。
/// </summary>
public sealed class StringPatternProfiler
{
    private const int MaxExamplesPerTemplate = 3;

    private readonly Dictionary<string, TemplateAccumulator> _templates = new(StringComparer.Ordinal);
    private long _total;

    /// <summary>流式添加一个字符串样本。空值 / 空白串不计入。</summary>
    public void Add(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (string.IsNullOrWhiteSpace(value)) return;

        _total++;
        string template = BuildTemplate(value);

        if (!_templates.TryGetValue(template, out var acc))
        {
            acc = new TemplateAccumulator();
            _templates[template] = acc;
        }
        acc.Count++;
        if (acc.Examples.Count < MaxExamplesPerTemplate)
        {
            // 避免与已有的示例完全重复（保留差异化的样例）
            if (!acc.Examples.Contains(value))
            {
                acc.Examples.Add(value);
            }
        }
    }

    /// <summary>
    /// 合并另一线程的 profiler（线程局部汇总时使用）：把 <paramref name="other"/> 的模板计数与样例
    /// 累加到当前实例，total 同步相加。Examples 不会重复添加，Count 直接累加。
    /// </summary>
    public void MergeFrom(StringPatternProfiler other)
    {
        if (other == null) return;
        _total += other._total;
        foreach (var (template, otherAcc) in other._templates)
        {
            if (!_templates.TryGetValue(template, out var acc))
            {
                acc = new TemplateAccumulator();
                _templates[template] = acc;
            }
            acc.Count += otherAcc.Count;
            foreach (var ex in otherAcc.Examples)
            {
                if (acc.Examples.Count >= MaxExamplesPerTemplate) break;
                if (!acc.Examples.Contains(ex))
                {
                    acc.Examples.Add(ex);
                }
            }
        }
    }

    /// <summary>构建 <see cref="StringPatternProfile"/>，按覆盖率降序，含 OtherCount。</summary>
    public StringPatternProfile Build(int topN = 8)
    {
        var profile = new StringPatternProfile { TotalSamples = _total };
        if (_total == 0) return profile;

        var sorted = _templates
            .OrderByDescending(kv => kv.Value.Count)
            .Take(topN)
            .Select(kv => new StringPatternTemplate
            {
                Template = kv.Key,
                Count = kv.Value.Count,
                Ratio = (double)kv.Value.Count / _total,
                Examples = kv.Value.Examples.ToList()
            })
            .ToList();

        profile.Patterns = sorted;
        if (sorted.Count > 0)
        {
            profile.DominantPattern = sorted[0];
        }

        long countedInTop = sorted.Sum(t => t.Count);
        profile.OtherCount = _total - countedInTop;
        return profile;
    }

    /// <summary>把字符串转换为模板（公开便于单值测试）。</summary>
    public static string BuildTemplate(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c >= '0' && c <= '9')
            {
                sb.Append('#');
            }
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                sb.Append('?');
            }
            else if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                sb.Append('*'); // 不会命中（上面两个分支已覆盖）
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private sealed class TemplateAccumulator
    {
        public long Count;
        public List<string> Examples = new();
    }
}
