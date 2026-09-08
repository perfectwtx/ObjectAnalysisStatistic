using System.Buffers;

namespace ObjectAnalyzer.Analysis;

internal sealed class AnalyzerThreadState
{
    public Dictionary<string, FieldStatistic> StatsMap { get; } = new();
    public Dictionary<string, InternalFieldMetrics> MetricsMap { get; } = new();
    public long TotalObjects { get; set; }
    public ArrayBufferWriter<byte> CanonicalWriter { get; } = new();

    // ── 深度分析 分析深度增强（懒创建：仅 features 启用且确有相关数据时分配）──
    /// <summary>字段相关性收集器：每行 (field → double?) 样本，超容量时 Reservoir Sampling。</summary>
    public FieldCorrelationCollector? CorrelationCollector { get; set; }
    /// <summary>字符串模式归纳器：按字段索引。</summary>
    public Dictionary<string, StringPatternProfiler>? StringPatterns { get; set; }
    /// <summary>Unicode 卫生报告：按字段索引（线程局部累加，主线程 MergeInto）。</summary>
    public Dictionary<string, UnicodeHygieneReport>? UnicodeHygiene { get; set; }
}
