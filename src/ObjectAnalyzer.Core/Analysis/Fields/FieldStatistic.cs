namespace ObjectAnalyzer.Analysis;

using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

public class FieldStatistic
{
    public string FieldName { get; set; } = "";
    [JsonIgnore]
    public FieldPath Path => FieldPath.Parse(FieldName);
    public long Count { get; set; }
    public double Coverage { get; set; } // 字段在所有对象中的覆盖率 (0.0 - 1.0)

    /// <summary>
    /// 字段值为显式 null 的记录数（Count 的子集）。与"字段不存在"（键缺失，
    /// 即 TotalObjects - Count）相对——两者是未有效覆盖的两种不同原因。
    /// </summary>
    public long NullCount { get; set; }

    /// <remarks>
    /// 由分析器在内部填充，外部调用者应视为只读。
    /// </remarks>
    public Dictionary<string, long> TypeCounts { get; set; } = new();

    /// <remarks>
    /// 由分析器在内部填充，外部调用者应视为只读。
    /// </remarks>
    public Dictionary<RecordValue, long> ValueCounts { get; set; } = new();

    /// <summary>Occurrences omitted from ValueCounts; never represented as a synthetic data value.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long OtherValueCount { get; set; }

    /// <summary>True only when every observed numeric value is integral, including untracked values.</summary>
    public bool NumbersAreIntegral { get; set; } = true;
    /// <summary>Lossless numeric output representations, accumulated over all observed numeric values.</summary>
    public bool NumbersFitInt64 { get; set; } = true;
    public bool NumbersFitDecimal { get; set; } = true;
    public bool NumbersFitDouble { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ValueCountsTruncated { get; set; }

    // 数值统计
    public double? MinNumber { get; set; }
    public double? MaxNumber { get; set; }
    public double? AvgNumber { get; set; }
    public double? SumNumber { get; set; }

    /// <summary>样本标准差（Welford 在线算法，n-1 分母）；样本数 &lt; 2 时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StdDevNumber { get; set; }

    /// <summary>近似中位数 P50（可合并 t-digest 草图）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MedianNumber { get; set; }

    /// <summary>近似 P90。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P90Number { get; set; }

    /// <summary>近似 P95。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P95Number { get; set; }

    /// <summary>近似 P99。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P99Number { get; set; }

    /// <summary>对数定宽（2 的幂）直方图分桶，按桶下界升序，跳过空桶；无数值时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NumericHistogramBucket>? Histogram { get; set; }

    // ── 日期/时间深度剖析（P3）──

    /// <summary>解析为 DateTimeOffset 的样本数；无数值或非时间字段时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DateTimeCount { get; set; }

    /// <summary>最小时间戳（统一为 DateTimeOffset；无时区输入按 UTC 解释）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MinDateTime { get; set; }

    /// <summary>最大时间戳。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MaxDateTime { get; set; }

    /// <summary>时间戳直方图（自动按跨度切档：秒/分/时/天/周/月/年）。无时间值时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DateHistogramBucket>? DateHistogram { get; set; }

    // ── 异常值检测（P3）──

    /// <summary>异常值方法（"iqr" / "zscore" / "3sigma"）；流式样本数 &lt; 4 时不计算（避免小样本抖动）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutlierMethod { get; set; }

    /// <summary>异常值个数（流式收集，基于对数直方图近似归类）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutlierCount { get; set; }

    /// <summary>异常值占比（0.0~1.0，相对于非空数值样本数）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutlierRatio { get; set; }

    /// <summary>异常值判定区间（紧凑文本，如 "[1.5, 95.0]" / "z±2.58"）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutlierBounds { get; set; }

    /// <summary>字符串长度统计</summary>
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>平均字符串长度（流式累加 / 出现次数）；无字符串值时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AvgLength { get; set; }

    /// <summary>
    /// distinct 值计数：唯一值未截断时为精确计数；超过跟踪阈值时为 HyperLogLog 近似估计
    /// （此时 <see cref="DistinctApproximate"/> 为 true）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DistinctCount { get; set; }

    /// <summary><see cref="DistinctCount"/> 是否为近似值（HyperLogLog 估计）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DistinctApproximate { get; set; }

    // 智能语义推断（例如 "Email", "UUID", "DateTime", "IPv4", "URL"）
    public string? SemanticType { get; set; }

    // 以下为关于默认值的属性
    public string PrimaryType { get; set; } = "Unknown";
    public bool NullAppeared { get; set; }
    public string? DefaultValueRepresentation { get; set; }

    // 规则校验相关
    public FieldRule? AppliedRule { get; set; }

    // 字段值转换相关
    /// <summary>转换失败次数（保留原始值并记录）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TransformFailureCount { get; set; }

    /// <summary>应用的转换类型（如 jwt / aes / base64 / url）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransformType { get; set; }

    /// <summary>数值越界出现次数（规则 minValue/maxValue，热路径精确计数，不受唯一值截断影响）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long RangeViolationCount { get; set; }

    // Fix #8: EnumViolations 封装为只读接口，防止外部调用者意外修改分析结果
    // 注意：必须可写（非 readonly）以便 P6.2 --resume 从快照反序列化（System.Text.Json 不支持 readonly 字段反序列化）。
    [JsonInclude]
    [JsonPropertyName("enumViolations")]
    private List<string> _enumViolations = new();

    /// <summary>
    /// 不符合枚举约束的实际值列表。只读，由分析器内部填充。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EnumViolations => _enumViolations;

    /// <summary>
    /// 供分析器内部使用，向枚举违规列表追加一个违规值。
    /// </summary>
    internal void AddEnumViolation(string value) => _enumViolations.Add(value);

    /// <summary>
    /// 出站打码用的副本底版：<see cref="object.MemberwiseClone"/> 浅拷贝全部标量统计
    /// （<see cref="PrimaryType"/>、日期剖析、异常值、RangeViolationCount 等未来新增属性自动携带），
    /// 可变集合（TypeCounts / ValueCounts / 两个直方图 / 枚举违规列表）一律换成新实例并断开与原对象的共享——
    /// 打码副本必须是"可安全分发的独立副本"，调用方随后只需重填打码后的 ValueCounts 键与枚举违规值。
    /// </summary>
    internal FieldStatistic CloneForRedaction()
    {
        var clone = (FieldStatistic)MemberwiseClone();
        clone.TypeCounts = new Dictionary<string, long>(TypeCounts);
        clone.ValueCounts = new Dictionary<RecordValue, long>();
        clone.Histogram = Histogram is null
            ? null
            : Histogram.Select(b => new NumericHistogramBucket { From = b.From, To = b.To, Count = b.Count }).ToList();
        clone.DateHistogram = DateHistogram is null
            ? null
            : DateHistogram.Select(b => new DateHistogramBucket { From = b.From, To = b.To, Count = b.Count }).ToList();
        clone._enumViolations = new List<string>();
        return clone;
    }

    /// <summary>格式化分位数摘要（"P50 x · P90 y · P95 z · P99 w"）；无分位数数据时返回 null。</summary>
    public string? FormatQuantiles()
    {
        if (MedianNumber is null || P90Number is null || P95Number is null || P99Number is null)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"P50 {MedianNumber:G4} · P90 {P90Number:G4} · P95 {P95Number:G4} · P99 {P99Number:G4}");
    }

    // ── P6.2 增量分析 / 断点续跑：FieldStatistic 级别合并 ──

    /// <summary>
    /// 把另一份 <see cref="FieldStatistic"/> 合并进本对象（FieldStatistic-level 合并）。
    ///
    /// 合并策略（针对已计算好的"快照式"统计，不接触流式草图）：
    /// - Count / Sum / 长度统计：直接相加
    /// - Min / Max：取极端
    /// - Mean / StdDev：使用 Welford-Knuth 风格的并行合并公式（利用 count + mean + stddev 重建 Σ(x-μ)² 即可）
    /// - TypeCounts / ValueCounts / SemanticCounts：按 key 累加
    /// - 直方图（Histogram / DateHistogram）：按桶下界累加
    /// - DistinctCount：取 max（HyperLogLog 草图未持久化，无法精确合并；选 max 是无信息损失的上界）
    /// - Median/P90/P95/P99：分位数不可逆——保留新数据分位数，并附带 warning 标签
    /// - SemanticType / PrimaryType / AppliedRule：保留旧值（确定性优先）
    /// - EnumViolations：合并（追加）
    ///
    /// 此方法不抛异常——空对象合并保持幂等；用于 AnalysisResult.FieldStatistics 跨多次增量 run 累积。
    /// </summary>
    public void MergeFrom(FieldStatistic other)
    {
        if (other is null) return;

        // 基本计数
        Count += other.Count;
        SumNumber = (SumNumber ?? 0) + (other.SumNumber ?? 0);
        RangeViolationCount += other.RangeViolationCount;
        TransformFailureCount += other.TransformFailureCount;
        NullAppeared = NullAppeared || other.NullAppeared;
        NullCount += other.NullCount;

        // Min / Max
        if (other.MinNumber is not null)
        {
            MinNumber = MinNumber is null ? other.MinNumber : Math.Min(MinNumber.Value, other.MinNumber.Value);
        }
        if (other.MaxNumber is not null)
        {
            MaxNumber = MaxNumber is null ? other.MaxNumber : Math.Max(MaxNumber.Value, other.MaxNumber.Value);
        }

        // Mean / StdDev（Welford 并行合并）
        if (other.AvgNumber is not null && other.Count > 0)
        {
            long n1 = Count - other.Count; // 原 this 的样本数
            long n2 = other.Count;
            if (n1 > 0 && AvgNumber is not null)
            {
                double mean1 = AvgNumber.Value;
                double mean2 = other.AvgNumber.Value;
                double newMean = (n1 * mean1 + n2 * mean2) / (n1 + n2);

                // 从 StdDevNumber (样本标准差，n-1 分母) 重建 Σ(x-μ)²
                double? m2 = null;
                if (StdDevNumber is not null && n1 > 1)
                {
                    m2 = StdDevNumber.Value * StdDevNumber.Value * (n1 - 1);
                }
                double? m2Other = null;
                if (other.StdDevNumber is not null && n2 > 1)
                {
                    m2Other = other.StdDevNumber.Value * other.StdDevNumber.Value * (n2 - 1);
                }

                if (m2.HasValue && m2Other.HasValue)
                {
                    double newM2 = m2.Value + m2Other.Value
                        + (mean1 - newMean) * (mean1 - newMean) * n1
                        + (mean2 - newMean) * (mean2 - newMean) * n2;
                    long newCount = n1 + n2;
                    AvgNumber = newMean;
                    if (newCount > 1)
                    {
                        StdDevNumber = Math.Sqrt(newM2 / (newCount - 1));
                    }
                }
                else
                {
                    // 某一方样本数 < 2，stddev 不可用；均值仍可计算
                    AvgNumber = newMean;
                    if (n1 <= 1) StdDevNumber = other.StdDevNumber;
                }
            }
            else
            {
                // 本对象原本无数值数据：直接采用 other
                AvgNumber = other.AvgNumber;
                StdDevNumber = other.StdDevNumber;
            }
        }

        // 分位数（不可逆合并，保留 new = other）
        if (other.MedianNumber is not null) MedianNumber = other.MedianNumber;
        if (other.P90Number is not null) P90Number = other.P90Number;
        if (other.P95Number is not null) P95Number = other.P95Number;
        if (other.P99Number is not null) P99Number = other.P99Number;

        // 长度统计
        if (other.MinLength is not null)
        {
            MinLength = MinLength is null ? other.MinLength : Math.Min(MinLength.Value, other.MinLength.Value);
        }
        if (other.MaxLength is not null)
        {
            MaxLength = MaxLength is null ? other.MaxLength : Math.Max(MaxLength.Value, other.MaxLength.Value);
        }
        if (other.AvgLength is not null && other.Count > 0)
        {
            // 平均长度 = Σlength / count；totalCount 已 += other.Count
            // 用旧 Σlength = AvgLength * 旧 count 重建
            long n1 = Count - other.Count;
            long n2 = other.Count;
            if (n1 > 0 && AvgLength is not null)
            {
                double sum1 = AvgLength.Value * n1;
                double sum2 = other.AvgLength.Value * n2;
                AvgLength = (sum1 + sum2) / (n1 + n2);
            }
            else
            {
                AvgLength = other.AvgLength;
            }
        }

        // DistinctCount（上界合并——HyperLogLog 草图未持久化时取 max 是无信息损失近似）
        if (other.DistinctCount is not null)
        {
            DistinctCount = DistinctCount is null
                ? other.DistinctCount
                : Math.Max(DistinctCount.Value, other.DistinctCount.Value);
        }
        DistinctApproximate = DistinctApproximate || other.DistinctApproximate;

        // TypeCounts
        foreach (var kv in other.TypeCounts)
        {
            TypeCounts[kv.Key] = TypeCounts.GetValueOrDefault(kv.Key) + kv.Value;
        }

        // ValueCounts
        if (other.ValueCountsTruncated) ValueCountsTruncated = true;
        OtherValueCount += other.OtherValueCount;
        NumbersAreIntegral &= other.NumbersAreIntegral;
        NumbersFitInt64 &= other.NumbersFitInt64;
        NumbersFitDecimal &= other.NumbersFitDecimal;
        NumbersFitDouble &= other.NumbersFitDouble;
        foreach (var kv in other.ValueCounts)
        {
            ValueCounts[kv.Key] = ValueCounts.GetValueOrDefault(kv.Key) + kv.Value;
        }

        // 数值直方图（对数桶，按 bucket.From 累加）
        if (other.Histogram is { Count: > 0 })
        {
            Histogram ??= new List<NumericHistogramBucket>();
            foreach (var b in other.Histogram)
            {
                var existing = Histogram.Find(x => x.From == b.From && x.To == b.To);
                if (existing is not null)
                {
                    existing.Count += b.Count;
                }
                else
                {
                    Histogram.Add(new NumericHistogramBucket { From = b.From, To = b.To, Count = b.Count });
                }
            }
        }

        // 日期统计
        if (other.MinDateTime is not null)
        {
            MinDateTime = MinDateTime is null || other.MinDateTime < MinDateTime ? other.MinDateTime : MinDateTime;
        }
        if (other.MaxDateTime is not null)
        {
            MaxDateTime = MaxDateTime is null || other.MaxDateTime > MaxDateTime ? other.MaxDateTime : MaxDateTime;
        }
        DateTimeCount = (DateTimeCount ?? 0) + (other.DateTimeCount ?? 0);

        if (other.DateHistogram is { Count: > 0 })
        {
            DateHistogram ??= new List<DateHistogramBucket>();
            foreach (var b in other.DateHistogram)
            {
                var existing = DateHistogram.Find(x => x.From == b.From && x.To == b.To);
                if (existing is not null)
                {
                    existing.Count += b.Count;
                }
                else
                {
                    DateHistogram.Add(new DateHistogramBucket { From = b.From, To = b.To, Count = b.Count });
                }
            }
        }

        // 异常值
        OutlierMethod ??= other.OutlierMethod;
        OutlierBounds ??= other.OutlierBounds;
        if (other.OutlierCount is not null)
        {
            OutlierCount = (OutlierCount ?? 0) + other.OutlierCount;
        }
        if (other.OutlierRatio is not null && Count > 0 && OutlierCount is not null)
        {
            OutlierRatio = (double)OutlierCount.Value / Count;
        }

        // 语义与规则（保留旧值——确定性优先；其它源只在新对象原本为空时填充）
        PrimaryType = string.IsNullOrEmpty(PrimaryType) || PrimaryType == "Unknown" ? other.PrimaryType : PrimaryType;
        SemanticType ??= other.SemanticType;
        DefaultValueRepresentation ??= other.DefaultValueRepresentation;
        AppliedRule ??= other.AppliedRule;
        TransformType ??= other.TransformType;

        // 枚举违规（追加）
        if (other.EnumViolations.Count > 0)
        {
            foreach (var v in other.EnumViolations) AddEnumViolation(v);
        }
    }
}
