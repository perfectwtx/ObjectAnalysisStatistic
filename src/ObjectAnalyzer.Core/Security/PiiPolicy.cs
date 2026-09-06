namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 一次分析运行的出站打码策略：字段名（flatten 后路径）→ 打码格式。
/// <para>
/// 架构定位：分析内核始终处理原始值；打码是"出站边界"的职责——任何结果离开服务
/// （API 响应、报告导出、数据预览、未来的新出站形态）都必须经过同一份策略打码。
/// 策略由 <see cref="Resolve"/> 从分析结果一次性解析，是所有出站打码的唯一判定来源，
/// 使用方（<see cref="PiiRedactor"/>、预览打码、未来的合规摘要等）不再各自重新推导。
/// </para>
/// </summary>
public sealed class PiiPolicy
{
    /// <summary>空策略：无任何敏感字段（出站打码零成本直通）。</summary>
    public static readonly PiiPolicy Empty = new(new Dictionary<string, PiiMaskFormat>(StringComparer.Ordinal));

    private PiiPolicy(IReadOnlyDictionary<string, PiiMaskFormat> masksByField) => MasksByField = masksByField;

    /// <summary>字段名（flatten 后路径）→ 打码格式。</summary>
    public IReadOnlyDictionary<string, PiiMaskFormat> MasksByField { get; }

    /// <summary>是否不含任何敏感字段。</summary>
    public bool IsEmpty => MasksByField.Count == 0;

    /// <summary>敏感字段数量（响应回显 / 合规摘要复用）。</summary>
    public int FieldCount => MasksByField.Count;

    /// <summary>
    /// 从分析结果解析出站打码策略。字段级优先级与 <see cref="PiiDetector.TryGetMaskFormat"/> 一致：
    /// 规则 redact:false 白名单（豁免，误报逃生口）→ 规则 pii 声明（自定义格式）→
    /// 内置语义类型（手机号/身份证/银行卡/姓名/邮箱/SSN/IBAN/护照）。
    /// </summary>
    public static PiiPolicy Resolve(AnalysisResult result)
    {
        var masks = new Dictionary<string, PiiMaskFormat>(StringComparer.Ordinal);
        foreach (var stat in result.FieldStatistics)
        {
            var mask = PiiDetector.TryGetMaskFormat(stat.SemanticType, stat.AppliedRule);
            if (mask != null)
                masks[stat.FieldName] = mask;
        }

        return masks.Count == 0 ? Empty : new PiiPolicy(masks);
    }

    /// <summary>对某字段的一个字符串值应用打码；非敏感字段原样返回。</summary>
    public string? Apply(string fieldName, string? value)
    {
        if (value is null)
            return null;
        return MasksByField.TryGetValue(fieldName, out var mask) ? mask.Apply(value) : value;
    }

    /// <summary>逐字段输出解析后的打码元数据（按字段名排序），供 API 响应回显 / 异步快照复用。</summary>
    public IReadOnlyList<PiiFieldSummary> Summaries() =>
        MasksByField
            .Select(kv => new PiiFieldSummary(
                Field: kv.Key,
                Category: kv.Value.Category.ToString(),
                Label: kv.Value.Label,
                HighSeverity: kv.Value.HighSeverity,
                IsCustom: kv.Value.IsCustom,
                MaskChar: kv.Value.MaskChar,
                MaskAll: kv.Value.MaskAll,
                KeepPrefix: kv.Value.KeepPrefix,
                KeepSuffix: kv.Value.KeepSuffix))
            .OrderBy(s => s.Field, StringComparer.Ordinal)
            .ToList();
}

/// <summary>单个敏感字段的打码元数据摘要（label / 分类 / 打码格式），纯展示用途，不参与打码计算。</summary>
/// <param name="Field">字段名（flatten 后路径）。</param>
/// <param name="Category">PII 分类（Phone / IdCard / … / Custom；Custom = 规则声明的业务敏感字段）。</param>
/// <param name="Label">分类展示标签：内置分类为中文（手机号等），自定义字段为规则 label（缺省「业务敏感字段」）。</param>
/// <param name="IsCustom">是否来自规则 pii 声明（true = 自定义格式；false = 内置语义映射）。</param>
public sealed record PiiFieldSummary(
    string Field,
    string Category,
    string Label,
    bool HighSeverity,
    bool IsCustom,
    string MaskChar,
    bool MaskAll,
    int KeepPrefix,
    int KeepSuffix);
