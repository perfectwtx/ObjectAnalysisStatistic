using System;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 个人敏感信息 (PII) 分类。
/// </summary>
public enum PiiCategory
{
    None,
    Phone,
    IdCard,
    BankCard,
    Name,
    Email
}

/// <summary>
/// PII 分类与打码工具：将字段的 SemanticType 映射为敏感分类，并提供确定性打码规则
/// （手机号 138****1234、身份证 110101********7528、银行卡 6222********1234、姓名 张*三、邮箱 ab***@example.com）。
/// 打码不可逆，且对已打码值重复打码结果稳定（幂等）。
/// </summary>
public static class PiiDetector
{
    /// <summary>
    /// 由语义类型映射为 PII 分类；非敏感语义返回 <see cref="PiiCategory.None"/>。
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
            _ => PiiCategory.None
        };
    }

    /// <summary>该语义类型是否属于个人敏感信息。</summary>
    public static bool IsPii(string? semanticType) => FromSemanticType(semanticType) != PiiCategory.None;

    /// <summary>高敏感分类（手机号 / 身份证 / 银行卡）。</summary>
    public static bool IsHighSeverity(PiiCategory category) =>
        category is PiiCategory.Phone or PiiCategory.IdCard or PiiCategory.BankCard;

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
            _ => "无"
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
            _ => value
        };
    }

    private static string MaskDigits(string value, int prefixKeep, int suffixKeep)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        int len = value.Length;
        int keep = prefixKeep + suffixKeep;
        if (len <= keep)
        {
            // 值太短：仅保留前后缀中可行的部分，其余显式打码
            int mid = Math.Min(prefixKeep, len);
            var sb = new System.Text.StringBuilder(len);
            sb.Append(value.AsSpan(0, mid));
            sb.Append('*', Math.Max(0, len - mid));
            return sb.ToString();
        }

        var buffer = new System.Text.StringBuilder(len);
        buffer.Append(value.AsSpan(0, prefixKeep));
        buffer.Append('*', len - keep);
        buffer.Append(value.AsSpan(len - suffixKeep, suffixKeep));
        return buffer.ToString();
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