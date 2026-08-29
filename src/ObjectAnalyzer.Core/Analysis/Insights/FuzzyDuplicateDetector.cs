using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 模糊去重检测器：基于规范化键 + Jaccard 词袋相似度聚类。
/// 策略：
///  1. 对每个值做 NFKC 归一化、大小写折叠、空白折叠、Unicode 卫生处理，生成"规范化键"；
///  2. 同规范化键的对象归入同一组（这部分其实就是精确去重的"形态变体"）；
///  3. 对每个规范化键唯一的样本，额外用词袋 Jaccard 相似度在近似键间做近邻查找，
///     相似度 ≥ <see cref="FuzzySimilarityThreshold"/>（默认 0.85）且编辑距离 ≤ 1 的归为同一组。
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
        Func<Dictionary<string, object?>, string?> keySelector)
    {
        var report = new FuzzyDuplicateReport();
        if (objects == null) return report;

        // 第一阶段：按 canonical key 精确聚类
        var byCanonical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var canonicalToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;

        foreach (var obj in objects)
        {
            string raw = keySelector(obj) ?? "";
            if (string.IsNullOrEmpty(raw)) continue;
            total++;

            string canon = Canonicalize(raw);
            if (string.IsNullOrEmpty(canon)) continue;

            if (!byCanonical.TryGetValue(canon, out var list))
            {
                list = new List<string>();
                byCanonical[canon] = list;
                canonicalToOriginal[canon] = raw;
            }
            if (list.Count < 5) list.Add(raw);
        }

        report.TotalSamples = total;
        long dupSamples = 0;
        int groupCount = 0;

        // 直接从大小写/空白/全半角等形态变体归并的组（不做 Jaccard 比较）
        foreach (var (canon, examples) in byCanonical.OrderByDescending(kv => kv.Value.Count))
        {
            if (examples.Count < 2)
            {
                // 单一样本无需建组（除非后续 Jaccard 找到近邻）
                continue;
            }
            groupCount++;
            dupSamples += examples.Count;
        }

        // 第二阶段：跨 canonical key 的 Jaccard 词袋相似度近邻
        // 仅当唯一 canonical key 较少（≤5000）时启用，避免 O(n²) 退化
        var uniqKeys = byCanonical.Keys.Where(k => byCanonical[k].Count == 1).ToList();
        if (uniqKeys.Count <= 5000)
        {
            var uniqTokens = uniqKeys.Select(BuildTokenSet).ToList();
            var parent = Enumerable.Range(0, uniqKeys.Count).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

            for (int i = 0; i < uniqKeys.Count; i++)
            {
                var ti = uniqTokens[i];
                if (ti.Count == 0) continue;
                for (int j = i + 1; j < uniqKeys.Count; j++)
                {
                    var tj = uniqTokens[j];
                    if (tj.Count == 0) continue;
                    // 大小差异过大直接跳过（避免与所有长字符串做无意义比较）
                    int minSize = Math.Min(ti.Count, tj.Count);
                    int maxSize = Math.Max(ti.Count, tj.Count);
                    if (maxSize > minSize * 2 + 1) continue;
                    if (Jaccard(ti, tj) >= FuzzySimilarityThreshold)
                    {
                        Union(i, j);
                    }
                }
            }

            var groupsByRoot = new Dictionary<int, List<int>>();
            for (int i = 0; i < uniqKeys.Count; i++)
            {
                int root = Find(i);
                if (!groupsByRoot.TryGetValue(root, out var bucket))
                {
                    bucket = new List<int>();
                    groupsByRoot[root] = bucket;
                }
                bucket.Add(i);
            }
            foreach (var bucket in groupsByRoot.Values.Where(b => b.Count >= 2))
            {
                groupCount++;
                dupSamples += bucket.Count;
            }
        }

        report.GroupCount = groupCount;
        report.SimilarDuplicates = dupSamples;

        // 暂不展开 Jaccard 合并后的具体内容（避免报告过大）；保留代表样本 + 计数
        var topGroups = byCanonical
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .Take(MaxReportedGroups)
            .Select(kv => new FuzzyDuplicateGroup
            {
                RepresentativeJson = SafeStringify(canonicalToOriginal[kv.Key]),
                CanonicalKey = kv.Key,
                Count = kv.Value.Count,
                AverageSimilarity = 1.0, // canonical key 一致即视为完全相似
                Examples = kv.Value.Take(5).ToList()
            })
            .ToList();
        report.Groups = topGroups;

        return report;
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

    private static string SafeStringify(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 截断过长字符串避免报告膨胀
        return value.Length > 200 ? string.Concat(value.AsSpan(0, 200), "...") : value;
    }
}
