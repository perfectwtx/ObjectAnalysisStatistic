using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Web;

/// <summary>
/// 预检（方案 B + 方案 D 共享核心）：用一份小样本解析数据，再对比规则文件里的字段名，
/// 找出"看起来规则与数据完全不匹配"的场景。
///
/// 设计目标：
/// - 零副作用：仅读取数据样本，不修改任何全局状态；
/// - 独立可测：直接接收 <see cref="IEnumerable{Dictionary}"/> 与字段集合作为输入，
///   单元测试可在不落盘的情况下覆盖全部判定分支；
/// - 严格不抛异常：解析失败 / 文件无法访问 / 规则文件无 fields 时一律返回
///   <c>HasWarning = false</c>，把"我读不出来"与"我读到但没有匹配"区分开。
/// </summary>
public static class PreflightChecker
{
    /// <summary>单次预检最多解析的样本条数。足够覆盖字段名探测，又不浪费内存。</summary>
    public const int DefaultSampleSize = 20;

    /// <summary>预检中向用户展示的样本字段名上限（避免一次性刷出上百个嵌套字段）。</summary>
    public const int MaxDisplayedSampleFields = 20;

    /// <summary>
    /// 入口：解析数据文件取样 → 与规则文件里的字段名做大小写不敏感比对。
    /// 返回结构化的 <see cref="PreflightResult"/>，由调用方决定如何渲染（模态框 / 横幅 / 仅日志）。
    /// </summary>
    /// <param name="dataFilePath">数据文件临时路径（Web UI 已落到 %TEMP% 的副本）。</param>
    /// <param name="rules">已反序列化的规则文件；为 null 时直接返回 no warning。</param>
    /// <param name="parseOptions">与正式分析一致的解析选项（含 valueParsers / 展平 / 采样等）。</param>
    /// <param name="format">检测到的文件格式（excel / excel-json / jsonl / ...）。</param>
    /// <param name="sampleSize">取样条数，默认 <see cref="DefaultSampleSize"/>。</param>
    public static PreflightResult Check(
        string dataFilePath,
        global::ObjectAnalyzer.Analysis.AnalysisRules? rules,
        ParseOptions parseOptions,
        string format,
        int sampleSize = DefaultSampleSize)
    {
        // 没有规则文件 → 不属于"用错规则文件"场景，零警告
        if (rules == null || rules.Fields.Count == 0)
        {
            return PreflightResult.NoWarning();
        }

        List<string> sampleFields;
        try
        {
            sampleFields = CollectSampleFields(dataFilePath, parseOptions, format, sampleSize);
        }
        catch
        {
            // 解析失败：交给正式分析自己报错；预检这里不阻断
            return PreflightResult.NoWarning();
        }

        return Build(rules.Fields.Keys, sampleFields);
    }

    /// <summary>
    /// 纯计算入口：给定"规则字段名集合"与"样本字段名集合"，返回预检结果。
    /// 单元测试只覆盖这一段，不依赖文件 IO。
    /// </summary>
    /// <remarks>
    /// 大小写不敏感比对（与 <see cref="FieldTransformProcessor"/> 的 _selectedFields 行为一致）；
    /// 当样本字段为空（如空文件 / 单条空对象）也视为"无匹配"，因为我们没法验证规则是否适用。
    /// </remarks>
    public static PreflightResult Build(
        IEnumerable<string> ruleFieldNames,
        IReadOnlyCollection<string> sampleFieldNames)
    {
        // 1) 规则字段去重（保留首次出现顺序），剔除空名
        var ruleFields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ruleFieldNames)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (seen.Add(f)) ruleFields.Add(f);
        }

        if (ruleFields.Count == 0)
        {
            return PreflightResult.NoWarning();
        }

        // 2) 样本字段去重 + 限长展示
        var sampleFields = new List<string>();
        var sampleSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in sampleFieldNames)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (sampleSeen.Add(f)) sampleFields.Add(f);
        }

        // 3) 比对：大小写不敏感 + 父路径匹配
        // 关键点：当规则字段带 parse:json 时，FieldTransformProcessor 会把值展平成 A.body.foo / A.body.bar
        // 等子字段，原始的 A.body 名字在结果中已被替换；但在预检阶段，解析器还没走 transform，
        // 样本里仍是原始列名（如 A）。为统一方案 B 与方案 D 的判定语义，约定：
        // 规则字段 X 命中结果字段 Y，当且仅当 Y == X（完全相等）或 Y 以 "X." 开头（X 是 Y 的父路径）。
        // 这样无论 transform 在哪个阶段触发，"规则字段确实覆盖到数据"这件事都能被识别。
        var matched = new List<string>();
        var unmatched = new List<string>();
        foreach (var f in ruleFields)
        {
            bool isMatched = sampleSeen.Contains(f);
            if (!isMatched)
            {
                string prefix = f + ".";
                foreach (var s in sampleFields)
                {
                    if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        isMatched = true;
                        break;
                    }
                }
            }
            if (isMatched) matched.Add(f); else unmatched.Add(f);
        }

        // 4) 判定：有规则字段 + 样本字段都拿到 + 一个都没匹配上 → 警告
        bool hasWarning = sampleFields.Count > 0 && matched.Count == 0;
        return new PreflightResult(
            HasWarning: hasWarning,
            RuleFields: ruleFields,
            MatchedRuleFields: matched,
            UnmatchedRuleFields: unmatched,
            SampleFields: sampleFields.Count > MaxDisplayedSampleFields
                ? sampleFields.GetRange(0, MaxDisplayedSampleFields)
                : sampleFields,
            TruncatedSampleFieldCount: Math.Max(0, sampleFields.Count - MaxDisplayedSampleFields));
    }

    /// <summary>
    /// 解析前 N 条记录，汇总出现过的所有字段名（嵌套字段已被 ParseOptions.Flatten 展平为 a.b.c）。
    /// 解析过程中产生的异常会原样抛出，由 <see cref="Check"/> 外层捕获并降级为 no warning。
    /// </summary>
    private static List<string> CollectSampleFields(
        string dataFilePath,
        ParseOptions parseOptions,
        string format,
        int sampleSize)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        int taken = 0;
        foreach (var record in FileParsers.ParseByFormat(dataFilePath, format, parseOptions))
        {
            foreach (var key in record.Keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (seen.Add(key)) ordered.Add(key);
            }
            taken++;
            if (taken >= sampleSize) break;
        }
        return ordered;
    }
}

/// <summary>
/// 预检输出：把"用错规则文件"这件事拆成结构化字段，让 UI 自由渲染。
/// </summary>
public sealed record PreflightResult(
    bool HasWarning,
    IReadOnlyList<string> RuleFields,
    IReadOnlyList<string> MatchedRuleFields,
    IReadOnlyList<string> UnmatchedRuleFields,
    IReadOnlyList<string> SampleFields,
    int TruncatedSampleFieldCount)
{
    public static PreflightResult NoWarning() =>
        new(false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), 0);
}
