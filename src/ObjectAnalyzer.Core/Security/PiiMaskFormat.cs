namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 某敏感字段应使用的打码格式：内置分类（手机号/身份证/银行卡/姓名/邮箱）或规则声明的自定义格式。
/// </summary>
public sealed class PiiMaskFormat
{
    /// <summary>内置分类；自定义业务字段为 <see cref="PiiCategory.Custom"/>。</summary>
    public PiiCategory Category { get; init; }

    /// <summary>展示标签（内置分类中文名或规则 label）。</summary>
    public string Label { get; init; } = "";

    /// <summary>是否属于高敏感（内置分类由类型决定，自定义字段由规则 highSeverity 决定）。</summary>
    public bool HighSeverity { get; init; }

    /// <summary>是否为规则声明的自定义格式。</summary>
    public bool IsCustom { get; init; }

    /// <summary>自定义：保留前缀字符数。</summary>
    public int KeepPrefix { get; init; }

    /// <summary>自定义：保留后缀字符数。</summary>
    public int KeepSuffix { get; init; }

    /// <summary>自定义：打码字符。</summary>
    public string MaskChar { get; init; } = "*";

    /// <summary>自定义：整值打码。</summary>
    public bool MaskAll { get; init; }

    /// <summary>对单个值应用本格式打码。</summary>
    public string Apply(string value)
    {
        if (!IsCustom)
        {
            return Category switch
            {
                PiiCategory.Phone => PiiDetector.Mask(value, PiiCategory.Phone),
                PiiCategory.IdCard => PiiDetector.Mask(value, PiiCategory.IdCard),
                PiiCategory.BankCard => PiiDetector.Mask(value, PiiCategory.BankCard),
                PiiCategory.Name => PiiDetector.Mask(value, PiiCategory.Name),
                PiiCategory.Email => PiiDetector.Mask(value, PiiCategory.Email),
                PiiCategory.Ssn => PiiDetector.Mask(value, PiiCategory.Ssn),
                PiiCategory.Iban => PiiDetector.Mask(value, PiiCategory.Iban),
                PiiCategory.Passport => PiiDetector.Mask(value, PiiCategory.Passport),
                _ => value
            };
        }

        if (MaskAll)
            return MaskAllChars(value, MaskChar);
        return PiiDetector.MaskDigits(value, Math.Max(0, KeepPrefix), Math.Max(0, KeepSuffix), MaskChar);
    }

    private static string MaskAllChars(string value, string maskChar)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (string.IsNullOrEmpty(maskChar))
            maskChar = "*";

        var buffer = new System.Text.StringBuilder(value.Length);
        while (buffer.Length < value.Length)
        {
            buffer.Append(maskChar);
        }
        buffer.Length = value.Length;
        return buffer.ToString();
    }
}
