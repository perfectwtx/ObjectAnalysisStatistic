using System.Globalization;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 数据预览的出站打码：与结果报告（<see cref="PiiRedactor"/>）消费同一份
/// <see cref="PiiPolicy"/>，对预览行里敏感字段的标量值打码。
/// 空策略返回原引用（零成本）；非敏感字段的行对象原样保留，不做无谓复制。
/// </summary>
/// <remarks>
/// 打码口径与 <see cref="PiiRedactor"/> 对重复对象 JSON 的处理一致：只处理标量值。
/// 字符串直接打码；数值等非字符串标量（如被解析成 long 的手机号）先转不变文化字符串再打码，
/// 杜绝数值形态的原文出站；对象 / 数组等复杂值不深入处理。
/// </remarks>
public static class PreviewRedactor
{
    public static IReadOnlyList<Dictionary<string, object?>> Redact(
        IReadOnlyList<Dictionary<string, object?>> preview, PiiPolicy policy)
    {
        if (policy.IsEmpty || preview.Count == 0)
            return preview;

        var rows = new List<Dictionary<string, object?>>(preview.Count);
        foreach (var row in preview)
        {
            Dictionary<string, object?>? masked = null;
            foreach (var (key, value) in row)
            {
                if (!policy.MasksByField.ContainsKey(key))
                    continue;

                masked ??= new Dictionary<string, object?>(row);
                masked[key] = MaskScalar(policy, key, value);
            }

            rows.Add(masked ?? row);
        }

        return rows;
    }

    private static object? MaskScalar(PiiPolicy policy, string field, object? value)
    {
        if (value is null)
            return null;
        if (value is string s)
            return policy.Apply(field, s);
        if (value is bool or char or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly)
        {
            return policy.Apply(field, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        return value;
    }
}
