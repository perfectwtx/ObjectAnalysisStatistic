using System.Text;

namespace ObjectAnalyzer.Analysis;

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
    private readonly AnalysisResourceLimits _limits;
    private readonly RetainedPayloadBudget? _payload;
    public bool ExamplesTruncated { get; private set; }
    public StringPatternProfiler(AnalysisResourceLimits? limits = null) : this(limits ?? new(), null) { }
    internal StringPatternProfiler(AnalysisResourceLimits limits, RetainedPayloadBudget? payload)
    { limits.Validate(); _limits = limits; _payload = payload; }
    private TemplateAccumulator GetTemplate(string template)
    {
        if (_templates.TryGetValue(template, out var existing)) return existing;
        if (_templates.Count >= _limits.MaxPatternTemplates) throw new AnalysisResourceLimitException("pattern-templates", _limits.MaxPatternTemplates);
        _payload?.Reserve(64 + template.Length * 2L);
        var created = new TemplateAccumulator(); _templates.Add(template, created); return created;
    }
    private void AddExample(TemplateAccumulator acc, string value)
    {
        if (value.Length > _limits.MaxExampleCharacters)
        { value = value[.._limits.MaxExampleCharacters]; ExamplesTruncated = true; }
        if (acc.Examples.Contains(value)) return;
        if (acc.Examples.Count >= MaxExamplesPerTemplate) { ExamplesTruncated = true; return; }
        _payload?.Reserve(value.Length * 2L + 24); acc.Examples.Add(value);
    }

    private const int MaxExamplesPerTemplate = 3;

    private readonly Dictionary<string, TemplateAccumulator> _templates = new(StringComparer.Ordinal);
    private long _total;

    /// <summary>流式添加一个字符串样本。空值 / 空白串不计入。</summary>
    public void Add(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (string.IsNullOrWhiteSpace(value)) return;

        if (value.Length > _limits.MaxPatternCharacters) throw new AnalysisResourceLimitException("pattern-characters", _limits.MaxPatternCharacters);
        _total++;
        string template = BuildTemplate(value);

        var acc = GetTemplate(template);
        acc.Count++;
        AddExample(acc, value);
    }

    /// <summary>
    /// 合并另一线程的 profiler（线程局部汇总时使用）：把 <paramref name="other"/> 的模板计数与样例
    /// 累加到当前实例，total 同步相加。Examples 不会重复添加，Count 直接累加。
    /// </summary>
    public void MergeFrom(StringPatternProfiler other)
    {
        if (other == null) return;
        _total += other._total;
        ExamplesTruncated |= other.ExamplesTruncated;
        foreach (var (template, otherAcc) in other._templates)
        {
            if (template.Length > _limits.MaxPatternCharacters) throw new AnalysisResourceLimitException("pattern-characters", _limits.MaxPatternCharacters);
            var acc = GetTemplate(template);
            acc.Count += otherAcc.Count;
            foreach (var ex in otherAcc.Examples) AddExample(acc, ex);
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
