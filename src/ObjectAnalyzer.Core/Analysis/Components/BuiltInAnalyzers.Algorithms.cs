using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

internal static partial class BuiltInAnalyzers
{
    // 常用语义识别正则表达式（预编译提升性能）
    private static readonly Regex RegexDateTime = new(
        @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex RegexUuid = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex RegexEmail = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex RegexIPv4 = new(
        @"^(\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex RegexUrl = new(
        @"^(https?|ftp)://[^\s/$.?#].[^\s]*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex RegexPhone = new(
        @"^1[3-9]\d{9}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // 中国大陆手机号
    private static readonly Regex RegexIdCard = new(
        @"^\d{15}$|^\d{17}[\dXx]$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // 中国大陆身份证（15/18 位）
    private static readonly Regex RegexBankCard = new(
        @"^\d{16,19}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // 银行卡号
    private static readonly Regex RegexSsn = new(
        @"^\d{3}-\d{2}-\d{4}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // 美国社会安全号 SSN（ddd-dd-dddd）
    private static readonly Regex RegexIban = new(
        @"^[A-Za-z]{2}\d{2}[A-Za-z0-9]{10,30}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // IBAN（紧凑格式，去除空格后匹配）
    private static readonly Regex RegexPassport = new(
        @"^[A-Za-z]{1,2}\d{6,9}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)); // 护照号（1-2 位字母 + 6-9 位数字，需字段名提示）

    // 字段名强提示：用于在值形态上辅助判别"姓名"语义（避免仅凭形态误判普通文本）
    private static readonly string[] NameFieldHints =
    {
        "name", "fullname", "realname", "username", "nickname", "displayname",
        "firstname", "lastname", "familyname", "givenname", "surname",
        "姓名", "用户名", "昵称", "联系人", "真实姓名"
    };

    // 护照号字段名提示（护照值形态较宽泛，必须字段名佐证以降低误报）
    private static readonly string[] PassportFieldHints =
    {
        "passport", "passportno", "passportnumber",
        "护照", "护照号"
    };


    private static List<NumericHistogramBucket> BuildHistogram(InternalFieldMetrics metrics)
    {
        var buckets = new List<NumericHistogramBucket>();

        if (metrics.Histogram != null)
        {
            foreach (var kv in metrics.Histogram.Where(h => h.Key < 0).OrderBy(h => h.Key))
            {
                // 负数桶 k=-e-1 表示 (-2^(e+1), -2^e]
                double to = -Math.Pow(2, -kv.Key - 1);
                double from = -Math.Pow(2, -kv.Key);
                buckets.Add(new NumericHistogramBucket { From = from, To = to, Count = kv.Value });
            }
        }

        if (metrics.ZeroCount > 0)
        {
            buckets.Add(new NumericHistogramBucket { From = 0, To = 0, Count = metrics.ZeroCount });
        }

        if (metrics.Histogram != null)
        {
            foreach (var kv in metrics.Histogram.Where(h => h.Key >= 0).OrderBy(h => h.Key))
            {
                // 正数桶 k 表示 [2^k, 2^(k+1))
                buckets.Add(new NumericHistogramBucket
                {
                    From = Math.Pow(2, kv.Key),
                    To = Math.Pow(2, kv.Key + 1),
                    Count = kv.Value
                });
            }
        }

        return buckets;
    }

    /// <summary>
    /// 构建按数据跨度自适应切档的时间戳直方图：先按总跨度选定显示桶尺寸，
    /// 再将流式阶段收集的 1 分钟粒度桶向上聚合到目标尺寸。
    /// 目标桶数 ≤ 24。
    /// </summary>
    private static List<DateHistogramBucket> BuildDateHistogram(
        DateTimeOffset min, DateTimeOffset max, Dictionary<long, long> minuteBuckets)
    {
        // 选定显示桶尺寸（任一桶的尺寸都用同一个值，保持等宽）
        var sample = NumericSketch.DateBucket(min, min, max, targetBuckets: 24);
        TimeSpan span = sample.span;
        long spanTicks = span.Ticks;
        long minTicks = min.UtcTicks;

        // 聚合：从 1 分钟桶 → 显示桶（对 minTicks 向下取整到 spanTicks 边界）
        long minBucketStart = (minTicks / spanTicks) * spanTicks;
        var aggregated = new Dictionary<long, long>();
        foreach (var (minuteKey, count) in minuteBuckets)
        {
            long minuteTicks = minuteKey * TimeSpan.TicksPerMinute;
            long displayBucket = (minuteTicks / spanTicks) * spanTicks;
            aggregated[displayBucket] = aggregated.GetValueOrDefault(displayBucket) + count;
        }

        // 补齐 [minBucketStart, max] 范围内所有空桶，便于报告看出"无活动"区间
        long maxTicks = (max.UtcTicks / spanTicks) * spanTicks;
        var result = new List<DateHistogramBucket>();
        for (long t = minBucketStart; t <= maxTicks; t += spanTicks)
        {
            long count = aggregated.GetValueOrDefault(t);
            var from = new DateTimeOffset(t, TimeSpan.Zero);
            var to = new DateTimeOffset(t + spanTicks, TimeSpan.Zero);
            result.Add(new DateHistogramBucket { From = from, To = to, Count = count });
        }
        return result;
    }

    /// <summary>
    /// 基于对数定宽直方图近似归类区间 [lower, upper] 外的样本数。
    /// 桶整体落在区间外时按整桶计入；桶跨越边界时按整桶计入（保守策略）。
    /// </summary>
    private static long CountOutsideHistogram(
        Dictionary<int, long>? histogram, long zeroCount, double lower, double upper)
    {
        long count = 0;
        if (histogram != null)
        {
            foreach (var kv in histogram)
            {
                // 重建桶上下界以判定是否完全在区间外
                double bucketLo, bucketHi;
                if (kv.Key < 0)
                {
                    // 负桶 k=-e-1：(-2^(e+1), -2^e]
                    int exp = -kv.Key - 1;
                    bucketLo = -Math.Pow(2, exp + 1);
                    bucketHi = -Math.Pow(2, exp);
                }
                else
                {
                    // 正桶 k：[2^k, 2^(k+1))
                    bucketLo = Math.Pow(2, kv.Key);
                    bucketHi = Math.Pow(2, kv.Key + 1);
                }

                // 桶完全在区间外：整桶计入；跨越边界：保守起见按整桶计入
                if (bucketHi < lower || bucketLo > upper)
                {
                    count += kv.Value;
                }
            }
        }
        if (zeroCount > 0 && (0 < lower || 0 > upper))
        {
            count += zeroCount;
        }
        return count;
    }

    private static void ObserveText(InternalFieldMetrics metrics, RecordValue value, string fieldName)
    {
        // 2. 字符串度量与语义测试
        string strVal = value.Scalar;
        if (value.Kind == RecordValueKind.String)
        {
            metrics.MinLength = Math.Min(metrics.MinLength, strVal.Length);
            metrics.MaxLength = Math.Max(metrics.MaxLength, strVal.Length);
            metrics.StringCount++;
            metrics.AddLength(strVal.Length);

            if (RegexUuid.IsMatch(strVal))
                metrics.SemanticCounts["UUID"] = metrics.SemanticCounts.GetValueOrDefault("UUID") + 1;
            else if (RegexEmail.IsMatch(strVal))
                metrics.SemanticCounts["Email"] = metrics.SemanticCounts.GetValueOrDefault("Email") + 1;
            else if (RegexDateTime.IsMatch(strVal) && TryParseDateTimeOffset(strVal, out _))
            {
                metrics.SemanticCounts["DateTime"] = metrics.SemanticCounts.GetValueOrDefault("DateTime") + 1;
            }
            else if (RegexIdCard.IsMatch(strVal) && PiiValidators.IsValidChineseIdCard(strVal))
                metrics.SemanticCounts["IdCard"] = metrics.SemanticCounts.GetValueOrDefault("IdCard") + 1;
            else if (RegexPhone.IsMatch(strVal))
                metrics.SemanticCounts["Phone"] = metrics.SemanticCounts.GetValueOrDefault("Phone") + 1;
            else if (RegexBankCard.IsMatch(strVal) && PiiValidators.IsValidBankCardLuhn(strVal))
                metrics.SemanticCounts["BankCard"] = metrics.SemanticCounts.GetValueOrDefault("BankCard") + 1;
            else if (RegexSsn.IsMatch(strVal))
                metrics.SemanticCounts["Ssn"] = metrics.SemanticCounts.GetValueOrDefault("Ssn") + 1;
            else if (IsIbanValue(strVal))
                metrics.SemanticCounts["Iban"] = metrics.SemanticCounts.GetValueOrDefault("Iban") + 1;
            else if (RegexPassport.IsMatch(strVal) && ContainsFieldHint(fieldName, PassportFieldHints))
                metrics.SemanticCounts["Passport"] = metrics.SemanticCounts.GetValueOrDefault("Passport") + 1;
            else if (RegexIPv4.IsMatch(strVal))
                metrics.SemanticCounts["IPv4"] = metrics.SemanticCounts.GetValueOrDefault("IPv4") + 1;
            else if (RegexUrl.IsMatch(strVal))
                metrics.SemanticCounts["URL"] = metrics.SemanticCounts.GetValueOrDefault("URL") + 1;
            else if (LooksLikeHumanName(strVal, fieldName))
                metrics.SemanticCounts["Name"] = metrics.SemanticCounts.GetValueOrDefault("Name") + 1;
        }
    }

    /// <summary>IBAN 判定：去除空格后匹配紧凑形态，再按 MOD 97-10 验证校验位。</summary>
    private static bool IsIbanValue(string value)
    {
        if (value.Length < 15 || value.Length > 39)
            return false;

        string compact = value.Replace(" ", "");
        return RegexIban.IsMatch(compact) && PiiValidators.IsValidIbanMod97(compact);
    }

    /// <summary>
    /// 字段名包含提示词即命中（大小写不敏感）。用于 passport 等前后缀皆常见的字段
    /// （passport_no / myPassport / 护照号），提示词本身语义足够明确，无误报风险。
    /// </summary>
    private static bool ContainsFieldHint(string fieldName, string[] hints)
    {
        foreach (string hint in hints)
        {
            if (fieldName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool LooksLikeHumanName(string value, string fieldName)
    {
        if (!IsNameFieldName(fieldName))
            return false;

        if (value.Length < 2 || value.Length > 40)
            return false;

        int letterCount = 0;
        foreach (char c in value)
        {
            if (char.IsLetter(c))
            {
                letterCount++;
                continue;
            }
            if (c is ' ' or '-' or '.')
                continue;
            return false;
        }
        return letterCount > 0;
    }

    private static bool IsNameFieldName(string fieldName)
    {
        foreach (string hint in NameFieldHints)
        {
            int hintLength = hint.Length;
            if (fieldName.Length < hintLength)
                continue;
            if (!fieldName.EndsWith(hint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fieldName.Length == hintLength)
                return true;
            char before = fieldName[fieldName.Length - hintLength - 1];
            if (before is '.' or '_' or '-')
                return true;
        }
        return false;
    }

    /// <summary>
    /// 解析日期时间字符串为 DateTimeOffset。支持 ISO 8601 三种形态：
    /// 带时区（"Z" / "+08:00"）、无时区（按 UTC 解释）、纯日期（按 UTC 00:00:00）。
    /// 解析失败返回 false。
    /// </summary>
    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            result = parsed;
            return true;
        }
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dateOnly))
        {
            result = new DateTimeOffset(dateOnly, TimeSpan.Zero);
            return true;
        }
        result = default;
        return false;
    }


    private static void CompleteNumeric(InternalFieldMetrics metrics, FieldStatistic stat)
    {
        if (metrics.NumberCount > 0)
        {
            stat.MinNumber = metrics.MinNumber;
            stat.MaxNumber = metrics.MaxNumber;
            stat.SumNumber = metrics.SumNumber;
            stat.AvgNumber = metrics.NumMean;

            // Welford 样本标准差（n-1 分母）
            if (metrics.NumberCount >= 2)
            {
                stat.StdDevNumber = Math.Sqrt(metrics.NumM2 / (metrics.NumberCount - 1));
            }

            // 近似分位数（可合并 t-digest 草图）
            if (metrics.Quantiles != null && metrics.Quantiles.TotalWeight > 0)
            {
                stat.MedianNumber = metrics.Quantiles.Quantile(0.50);
                stat.P90Number = metrics.Quantiles.Quantile(0.90);
                stat.P95Number = metrics.Quantiles.Quantile(0.95);
                stat.P99Number = metrics.Quantiles.Quantile(0.99);
            }

            // 对数定宽直方图：负数桶升序 → 零桶 → 正数桶升序
            if (metrics.Histogram != null || metrics.ZeroCount > 0)
            {
                stat.Histogram = BuildHistogram(metrics);
            }

            // 异常值检测（IQR 1.5×）：复用 P25/P75 + 对数直方图近似归类
            if (metrics.NumberCount >= 4 && metrics.Quantiles != null)
            {
                double? p25 = metrics.Quantiles.Quantile(0.25);
                double? p75 = metrics.Quantiles.Quantile(0.75);
                if (p25.HasValue && p75.HasValue)
                {
                    double iqr = p75.Value - p25.Value;
                    double lower = p25.Value - 1.5 * iqr;
                    double upper = p75.Value + 1.5 * iqr;
                    long outlierCount = CountOutsideHistogram(metrics.Histogram, metrics.ZeroCount, lower, upper);
                    stat.OutlierMethod = "iqr";
                    stat.OutlierBounds = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[{lower:G4}, {upper:G4}]");
                    stat.OutlierCount = outlierCount;
                    stat.OutlierRatio = metrics.NumberCount > 0 ? (double)outlierCount / metrics.NumberCount : 0;
                }
            }
        }

    }
    private static void CompleteText(InternalFieldMetrics metrics, FieldStatistic stat)
    {
        if (metrics.StringCount > 0)
        {
            stat.MinLength = metrics.MinLength;
            stat.MaxLength = metrics.MaxLength;
            stat.AvgLength = (double)metrics.LengthSum / metrics.StringCount;

            // 当某语义匹配数量达到字符串总数的 80% 以上时，标注其 SemanticType
            var topSemantic = metrics.SemanticCounts
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            if (topSemantic.Value > 0 && (double)topSemantic.Value / metrics.StringCount >= 0.8)
            {
                stat.SemanticType = topSemantic.Key;
            }
        }

    }
    private static void CompleteDate(InternalFieldMetrics metrics, FieldStatistic stat)
    {
        if (metrics.DateTimeCount > 0 && metrics.MinDateTime is not null && metrics.MaxDateTime is not null)
        {
            stat.DateTimeCount = metrics.DateTimeCount;
            stat.MinDateTime = metrics.MinDateTime;
            stat.MaxDateTime = metrics.MaxDateTime;
            if (metrics.DateHistogram is { Count: > 0 })
            {
                stat.DateHistogram = BuildDateHistogram(metrics.MinDateTime.Value, metrics.MaxDateTime.Value, metrics.DateHistogram);
            }
        }

    }
}
