using System.Globalization;

namespace ObjectAnalyzer.Parsing.Parsers;

/// <summary>
/// CSV 值的数值推断：把看起来是数字的字符串还原成 long / double。
/// </summary>
/// <remarks>
/// <para>为什么需要：CSV 是无类型格式，MiniExcel 取出的单元格值一律是 <see cref="string"/>。
/// 而 <c>ObjectAnalyzer.GetValueType</c> 只看 CLR 类型（<c>value is string → "String"</c>），
/// 于是 CSV 的数值列 <c>primaryType</c> 永远是 String —— 均值/标准差/分位数/直方图/相关性
/// 这一整套数值剖析对 CSV 全部失效，只有字符串指标（avgLength 等）能用。
/// JSON 走 <c>TryGetInt64/TryGetDouble</c> 没有这个问题，这里让 CSV 追平 JSON。</para>
///
/// <para>为什么保守：JSON 的数字是作者显式声明的（没加引号就是数字），CSV 没有这个信号，
/// 只能靠猜。<b>误转的代价是被转坏的那一列在报告里显示成另一个值（数据失真），
/// 漏转的代价只是维持现状</b>，两者不对等，所以凡有一丝歧义就保持字符串。</para>
/// </remarks>
internal static class CsvNumberInference
{
    /// <summary>
    /// 总位数上限（整数部分 + 小数部分）。超过这个长度按 ID / 卡号 / 雪花 ID 处理：
    /// double 只有 ~15-17 位有效数字，转过去必然丢精度，宁可不转。
    /// </summary>
    private const int MaxSignificantDigits = 15;

    /// <summary>长度硬上限：只是个快速否决，真正的判定看有效位数。</summary>
    private const int MaxLength = 40;

    /// <summary>
    /// 尝试把字符串转成数值；不满足保守条件时原样返回。
    /// </summary>
    /// <param name="value">解析器给出的单元格值。</param>
    /// <returns>long / double，或原始的 <paramref name="value"/>。</returns>
    public static object? TryConvert(object? value)
    {
        if (value is not string s) return value;
        if (s.Length == 0 || s.Length > MaxLength) return value;

        int i = 0;

        // 只接受可选的一个负号，不接受 '+'（"+86" 更像是区号而不是数字）
        if (s[0] == '-') i = 1;

        // ── 整数部分 ──
        int intStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        int intLen = i - intStart;
        if (intLen == 0) return value;                      // 不是数字开头（日期、编码、空…）
        if (intLen > 1 && s[intStart] == '0') return value; // 前导零：007 / 0123 → 编码而非数字

        // ── 小数部分（可选）──
        bool hasDot = i < s.Length && s[i] == '.';
        int fracLen = 0;
        if (hasDot)
        {
            i++;
            int fracStart = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            fracLen = i - fracStart;
            if (fracLen == 0) return value;                 // "12." 不是常规写法
        }

        // 还有多余字符 → 有别的含义，不猜：
        // 科学计数法（1E5 可能是物料编码）、千分位（1,234）、单位（42kg）、空白（" 42"）等
        if (i != s.Length) return value;

        if (intLen + fracLen > MaxSignificantDigits) return value;

        // 与 JSON 路径保持一致：整数优先 long，否则 double
        if (!hasDot
            && long.TryParse(s.AsSpan(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
        {
            return l;
        }

        return double.TryParse(
            s.AsSpan(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out double d)
            ? d
            : value;
    }
}
