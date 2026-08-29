namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 敏感证件/账号校验位验证器：用于在值形态命中后进一步确认，
/// 降低纯形态匹配带来的误报（如随机 18 位数字被误判为身份证号）。
/// 全部为确定性纯函数，仅在校验通过时才赋予对应 PII 语义。
/// </summary>
public static class PiiValidators
{
    // GB 11643-1999 身份证第 18 位校验码权重（ISO 7064 MOD 11-2）
    private static readonly int[] IdCardWeights = { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
    private const string IdCardCheckChars = "10X98765432";

    /// <summary>
    /// 中国大陆居民身份证号校验：15 位（无校验位，仅形态）返回 true；
    /// 18 位按 GB 11643 MOD 11-2 验证校验位。
    /// </summary>
    public static bool IsValidChineseIdCard(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        if (value.Length == 15)
            return true; // 一代证无校验位

        if (value.Length != 18)
            return false;

        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            if (value[i] < '0' || value[i] > '9')
                return false;
            sum += (value[i] - '0') * IdCardWeights[i];
        }

        char expected = IdCardCheckChars[sum % 11];
        char actual = char.ToUpperInvariant(value[17]);
        return actual == expected;
    }

    /// <summary>银行卡号 Luhn 校验（ISO/IEC 7812），13-19 位数字。</summary>
    public static bool IsValidBankCardLuhn(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 13 || value.Length > 19)
            return false;

        int sum = 0;
        bool doubleDigit = false;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            char c = value[i];
            if (c < '0' || c > '9')
                return false;

            int d = c - '0';
            if (doubleDigit)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// IBAN 校验位验证（ISO 7064 MOD 97-10）。入参应为去除空格后的紧凑格式
    /// （如 "DE89370400440532013000"），长度 15-34 位。
    /// </summary>
    public static bool IsValidIbanMod97(string compactValue)
    {
        if (string.IsNullOrEmpty(compactValue) || compactValue.Length < 15 || compactValue.Length > 34)
            return false;

        // 前 4 位（国家码 + 校验位）移到末尾，字母展开为数字后整体 mod 97 应余 1
        Span<char> rearranged = stackalloc char[compactValue.Length];
        compactValue.AsSpan(4).CopyTo(rearranged);
        compactValue.AsSpan(0, 4).CopyTo(rearranged[(compactValue.Length - 4)..]);

        long remainder = 0;
        foreach (char c in rearranged)
        {
            int chunk = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => -1
            };
            if (chunk < 0)
                return false;

            // 分段取模避免大数溢出：两位字母最多两位数，*100 安全
            remainder = chunk > 9
                ? (remainder * 100 + chunk) % 97
                : (remainder * 10 + chunk) % 97;
        }

        return remainder == 1;
    }
}
