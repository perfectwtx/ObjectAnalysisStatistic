using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 预检：用一份小样本解析数据，再对比规则文件里的字段名，
/// 找出"看起来规则与数据完全不匹配"的场景。
///
/// 设计目标：
/// - 零副作用：仅读取数据样本，不修改任何全局状态；
/// - 独立可测：<see cref="Build"/> 纯计算入口可直接在单元测试里覆盖全部判定分支，
///   <see cref="Check"/> 包装一层文件 IO 供 CLI / API / Web 复用；
/// - 严格不抛异常：解析失败 / 文件无法访问 / 规则文件无 fields 时一律返回
///   <c>HasWarning = false</c>，把"我读不出来"与"我读到但没有匹配"区分开。
///
/// 跨进程复用契约：所有入口返回 <see cref="PreflightResult"/>；调用方负责决定如何渲染
/// （Spectre 控制台 / OpenAPI JSON / Blazor 模态框），Core 不持有任何 IO 资源。
/// </summary>
public static class PreflightChecker
{
    /// <summary>单次预检最多解析的样本条数。足够覆盖字段名探测，又不浪费内存。</summary>
    public const int DefaultSampleSize = 20;

    /// <summary>预检中向用户展示的样本字段名上限（避免一次性刷出上百个嵌套字段）。</summary>
    public const int MaxDisplayedSampleFields = 20;

    /// <summary>
    /// 入口：解析数据文件取样 → 与规则文件里的字段名做大小写不敏感 + 父路径比对。
    /// 返回结构化的 <see cref="PreflightResult"/>，由调用方决定如何渲染。
    /// </summary>
    /// <param name="dataFilePath">数据文件路径（CLI / API / Web 各自管理生命周期）。</param>
    /// <param name="rules">已反序列化的规则文件；为 null 时直接返回 no warning。</param>
    /// <param name="parseOptions">与正式分析一致的解析选项（含 valueParsers / 展平 / 采样等）。</param>
    /// <param name="format">检测到的文件格式（excel / excel-json / jsonl / ...）。</param>
    /// <param name="sampleSize">取样条数，默认 <see cref="DefaultSampleSize"/>。</param>
    public static PreflightResult Check(
        string dataFilePath,
        AnalysisRules? rules,
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
    /// 大小写不敏感比对（与 <see cref="Rules.FieldTransformProcessor"/> 的 _selectedFields 行为一致）；
    /// 当样本字段为空（如空文件 / 单条空对象）也视为"无匹配"，因为我们没法验证规则是否适用。
    ///
    /// 父路径匹配：当规则字段带 <c>parse:json</c> 时，FieldTransformProcessor 会把值展平成
    /// <c>A.body.foo</c> / <c>A.body.bar</c> 等子字段，原始的 <c>A.body</c> 名字在结果中已被替换。
    /// 约定：规则字段 X 命中结果字段 Y，当且仅当 Y == X（完全相等）或 Y 以 "X." 开头
    /// （X 是 Y 的父路径），与方案 D（结果页兜底警告）保持同一套判定语义。
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
