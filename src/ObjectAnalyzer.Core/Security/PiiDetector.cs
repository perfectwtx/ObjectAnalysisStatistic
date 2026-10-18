namespace ObjectAnalyzer.Analysis;

/// <summary>
/// PII 分类与打码工具：将字段的 SemanticType（内置分类）或规则文件声明的自定义 PII 规则
/// 映射为打码格式，并提供确定性打码（手机号 138****1234、身份证 110101********7528、
/// 银行卡 6222********1234、姓名 张*三、邮箱 ab***@example.com，以及规则指定的自定义格式）。
/// 打码不可逆，且对已打码值重复打码结果稳定（幂等）。
/// </summary>
public static class PiiDetector
{
    /// <summary>
    /// 由语义类型映射为 PII 分类；非敏感语义返回 <see cref="PiiCategory.None"/>。
    /// 自定义业务字段不在此处判定，而是通过 <see cref="TryGetMaskFormat"/> 结合规则文件识别。
    /// </summary>
    public static PiiCategory FromSemanticType(string? semanticType)
    {
        return semanticType switch
        {
            "Phone" => PiiCategory.Phone,
            "IdCard" => PiiCategory.IdCard,
            "BankCard" => PiiCategory.BankCard,
            "Name" => PiiCategory.Name,
            "Email" => PiiCategory.Email,
            "Ssn" => PiiCategory.Ssn,
            "Iban" => PiiCategory.Iban,
            "Passport" => PiiCategory.Passport,
            _ => PiiCategory.None
        };
    }

    /// <summary>该语义类型是否属于个人敏感信息。</summary>
    public static bool IsPii(string? semanticType) => FromSemanticType(semanticType) != PiiCategory.None;

    /// <summary>高敏感分类（手机号 / 身份证 / 银行卡 / SSN / IBAN / 护照）。自定义分类的高危标记由规则决定。</summary>
    public static bool IsHighSeverity(PiiCategory category) =>
        category is PiiCategory.Phone or PiiCategory.IdCard or PiiCategory.BankCard
            or PiiCategory.Ssn or PiiCategory.Iban or PiiCategory.Passport;

    /// <summary>敏感分类的中文标签。</summary>
    public static string CategoryLabel(PiiCategory category)
    {
        return category switch
        {
            PiiCategory.Phone => "手机号",
            PiiCategory.IdCard => "身份证号",
            PiiCategory.BankCard => "银行卡号",
            PiiCategory.Name => "姓名",
            PiiCategory.Email => "邮箱",
            PiiCategory.Ssn => "社会保障号",
            PiiCategory.Iban => "国际银行账号",
            PiiCategory.Passport => "护照号",
            PiiCategory.Custom => "业务敏感字段",
            _ => "无"
        };
    }

    /// <summary>
    /// 解析某字段应使用的打码格式：规则文件 redact: false 白名单（误报逃生口）优先返回 null；
    /// 其次看规则文件声明的自定义 PII 规则；否则回退到语义类型映射的内置分类。
    /// 皆未命中返回 null（非敏感字段）。
    /// </summary>
    public static PiiMaskFormat? TryGetMaskFormat(string? semanticType, FieldRule? appliedRule)
    {
        if (appliedRule is { Redact: false })
            return null; // 字段级白名单：即使命中敏感语义/启发式也不打码

        if (appliedRule?.Pii is { } pii)
        {
            var mask = pii.Mask ?? new PiiMaskConfig { MaskAll = true };
            return new PiiMaskFormat
            {
                Category = PiiCategory.Custom,
                Label = string.IsNullOrWhiteSpace(pii.Label) ? CategoryLabel(PiiCategory.Custom) : pii.Label!,
                HighSeverity = pii.HighSeverity,
                IsCustom = true,
                MaskAll = mask.MaskAll,
                KeepPrefix = mask.KeepPrefix,
                KeepSuffix = mask.KeepSuffix,
                MaskChar = string.IsNullOrEmpty(mask.MaskChar) ? "*" : mask.MaskChar
            };
        }

        var category = FromSemanticType(semanticType);
        if (category == PiiCategory.None)
            return null;

        return new PiiMaskFormat
        {
            Category = category,
            Label = CategoryLabel(category),
            HighSeverity = IsHighSeverity(category)
        };
    }

    /// <summary>按语义类型对值打码；非敏感类型返回 null（由调用方决定是否保留原值）。</summary>
    public static string? TryMask(string value, string? semanticType)
    {
        var category = FromSemanticType(semanticType);
        if (category == PiiCategory.None)
            return null;
        return Mask(value, category);
    }

    /// <summary>打码单个敏感值（对 None 分类原样返回）。</summary>
    public static string Mask(string value, PiiCategory category)
    {
        return category switch
        {
            PiiCategory.Phone => MaskDigits(value, 3, 4),
            PiiCategory.IdCard => MaskDigits(value, 6, 4),
            PiiCategory.BankCard => MaskDigits(value, 4, 4),
            PiiCategory.Name => MaskName(value),
            PiiCategory.Email => MaskEmail(value),
            PiiCategory.Ssn => MaskSsn(value),
            PiiCategory.Iban => MaskDigits(value, 4, 3),
            PiiCategory.Passport => MaskDigits(value, 1, 3),
            _ => value
        };
    }

    /// <summary>SSN 打码：保留末 4 位，前缀固定掩码（123-45-6789 → ***-**-6789）。</summary>
    private static string MaskSsn(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= 4)
            return new string('*', value.Length);
        return "***-**-" + value[^4..];
    }

    private static string MaskDigits(string value, int prefixKeep, int suffixKeep)
        => MaskDigits(value, prefixKeep, suffixKeep, "*");

    internal static string MaskDigits(string value, int prefixKeep, int suffixKeep, string maskChar)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (string.IsNullOrEmpty(maskChar))
            maskChar = "*";

        int len = value.Length;
        long keep = (long)prefixKeep + suffixKeep;
        if (len <= keep)
        {
            // 值太短：仅保留前后缀中可行的部分，其余显式打码
            int mid = Math.Min(prefixKeep, Math.Max(0, len - 1));
            var sb = new System.Text.StringBuilder(len);
            sb.Append(value.AsSpan(0, mid));
            AppendRepeated(sb, maskChar, Math.Max(0, len - mid));
            return sb.ToString();
        }

        var buffer = new System.Text.StringBuilder(len);
        buffer.Append(value.AsSpan(0, prefixKeep));
        AppendRepeated(buffer, maskChar, (int)(len - keep));
        buffer.Append(value.AsSpan(len - suffixKeep, suffixKeep));
        return buffer.ToString();
    }

    private static void AppendRepeated(System.Text.StringBuilder sb, string s, int count)
    {
        if (count <= 0 || s.Length == 0)
            return;
        if (s.Length == 1)
        {
            sb.Append(s[0], count);
            return;
        }
        int end = sb.Length + count;
        while (sb.Length < end) sb.Append(s);
        sb.Length = end;
    }

    private static string MaskName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.Length == 1)
            return "*";
        if (value.Length == 2)
            return value[0] + "*";

        var buffer = new System.Text.StringBuilder(value.Length);
        buffer.Append(value[0]);
        buffer.Append('*', value.Length - 2);
        buffer.Append(value[^1]);
        return buffer.ToString();
    }

    private static string MaskEmail(string value)
    {
        int atIndex = value.IndexOf('@');
        if (atIndex <= 1)
            return "*".PadRight(value.Length, '*');

        var buffer = new System.Text.StringBuilder(value.Length + 3);
        buffer.Append(value.AsSpan(0, 2));
        buffer.Append("***");
        buffer.Append(value.AsSpan(atIndex));
        return buffer.ToString();
    }
}
