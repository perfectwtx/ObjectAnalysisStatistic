using System.Text;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 模糊去重检测器：基于规范化键 + Jaccard 词袋相似度聚类。
/// 策略：
///  1. 对每个值做 NFKC 归一化、大小写折叠、空白折叠、Unicode 卫生处理，生成"规范化键"；
///  2. 同规范化键的对象归入同一组（这部分其实就是精确去重的"形态变体"）；
///  3. 对每个规范化键唯一的样本，额外用词袋 Jaccard 相似度在近似键间做近邻查找，
///     相似度 ≥ <see cref="FuzzySimilarityThreshold"/>（默认 0.85）的归为同一组。
/// 适用场景：去除 "Alice" vs "alice" vs " ALICE "、"张伟" vs "张 伟" 这类形态变体。
/// </summary>
public static class FuzzyDuplicateDetector
{
    /// <summary>Jaccard 词袋相似度阈值，超过此值视为同一组。</summary>
    public const double FuzzySimilarityThreshold = 0.85;

    /// <summary>返回 TopN 组（按组样本数降序），超出合并为 "其他"（不返回具体内容）。</summary>
    public const int MaxReportedGroups = 200;

    /// <summary>把任意字符串归一化为 "canonical key"，用于形态变体聚类。</summary>
    public static string Canonicalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 1) NFKC 归一化（全/半角统一、兼容字符折叠）
        string s = value.Normalize(NormalizationForm.FormKC);
        // 2) 折叠空白（多空格 / Tab → 单空格）+ trim
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        // 3) 大小写折叠（invariant）
        s = s.ToLowerInvariant();
        return s;
    }

    /// <summary>
    /// 在 <paramref name="objects"/> 列表中按"代表性键"（规范化 + 主体语义）做模糊去重。
    /// <paramref name="keySelector"/> 选择用于去重的字段（通常选取关键标识字段，如 name / title）。
    /// </summary>
    public static FuzzyDuplicateReport Detect(
        IEnumerable<Dictionary<string, object?>> objects,
        Func<Dictionary<string, object?>, string?> keySelector,
        CancellationToken cancellationToken = default)
    {
        var collector = new FuzzyDuplicateCollector(new());
        if (objects is not null)
            foreach (var obj in objects)
            { cancellationToken.ThrowIfCancellationRequested(); collector.Observe(keySelector(obj)); }
        return collector.Complete(cancellationToken);
    }

    /// <summary>Jaccard 相似度 = |A∩B| / |A∪B|。两个 HashSet 已经按 canonical token 分词。</summary>
    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        int inter = 0;
        foreach (var t in a)
        {
            if (b.Contains(t)) inter++;
        }
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0.0 : (double)inter / union;
    }

    /// <summary>基于 Bigram 的字符级词袋：支持中文（CJK 按字符切分）与英文（按 bigram）。</summary>
    public static HashSet<string> BuildTokenSet(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value)) return tokens;

        string canon = Canonicalize(value);
        if (string.IsNullOrEmpty(canon)) return tokens;

        // 字符级 bigram
        for (int i = 0; i + 1 < canon.Length; i++)
        {
            tokens.Add(canon.Substring(i, 2));
        }
        // 单字符（短串时保底）
        if (canon.Length <= 3)
        {
            foreach (var c in canon) tokens.Add(c.ToString());
        }
        return tokens;
    }

}
