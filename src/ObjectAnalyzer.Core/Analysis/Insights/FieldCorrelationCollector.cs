using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段相关性收集器：分析阶段在 ObjectAnalyzer 中按需实例化，
/// 为每个对象保留一行（字段 → 数值）样本，超出上限时 Reservoir Sampling 随机采样。
/// </summary>
public sealed class FieldCorrelationCollector(int maxRows = 20_000, int? seed = null)
{
    private readonly int _maxRows = maxRows;
    private readonly Random _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    private long _seen;

    /// <summary>按字段名索引的样本（list[i] 与 list[j] 对应同一对象的两个字段值）。</summary>
    private readonly Dictionary<string, List<double?>> _samples = new(StringComparer.Ordinal);

    public int MaxRows => _maxRows;
    public long Seen => _seen;
    public IReadOnlyDictionary<string, List<double?>> Samples => _samples;

    /// <summary>在流式阶段调用：注册本次对象中某字段的数值（null 表示缺失或非数值）。</summary>
    public void RecordObject(IReadOnlyDictionary<string, double?> row)
    {
        _seen++;
        if (_samples.Count == 0)
        {
            // 第一次：直接建表
            foreach (var kv in row)
            {
                var list = new List<double?>(_maxRows) { kv.Value };
                _samples[kv.Key] = list;
            }
            return;
        }

        // 已有表：仅追加已知字段（未知字段首次出现则创建）
        foreach (var (field, value) in row)
        {
            if (!_samples.TryGetValue(field, out var list))
            {
                list = new List<double?>(_maxRows);
                for (int k = 0; k < _seen - 1; k++) list.Add(null);
                list.Add(value);
                _samples[field] = list;
            }
            else if (list.Count < _maxRows)
            {
                list.Add(value);
            }
            else
            {
                // Reservoir sampling
                int idx = _rng.Next(0, (int)Math.Min(int.MaxValue, _seen));
                if (idx < _maxRows) list[idx] = value;
            }
        }
        // 缺失字段补 null
        foreach (var field in _samples.Keys.ToList())
        {
            if (!row.ContainsKey(field))
            {
                var list = _samples[field];
                if (list.Count < _maxRows) list.Add(null);
            }
        }
    }

    public void Clear()
    {
        _samples.Clear();
        _seen = 0;
    }

    /// <summary>
    /// 把另一个线程的收集器的样本追加到当前实例。每个字段的样本列表按时间顺序拼接——
    /// 假设两个收集器对应的对象集合不重叠（并行分区处理同一数据集的不同切片）。
    /// 注意：本方法**不安全**用于两个独立数据集的合并（会破坏"对象内同序号字段对齐"的语义）。
    /// </summary>
    public void MergeFrom(FieldCorrelationCollector other)
    {
        if (other == null || other._seen == 0) return;
        long otherSeen = other._seen;
        long baseSeen = _seen;

        // 新字段（首次在 other 中出现）需要先在 self 中按 baseSeen 个 null 占位，再追加 other 的样本
        foreach (var (field, otherList) in other._samples)
        {
            if (!_samples.TryGetValue(field, out var selfList))
            {
                selfList = new List<double?>(_maxRows);
                for (int i = 0; i < baseSeen; i++) selfList.Add(null);
                selfList.AddRange(otherList.Take(_maxRows));
                _samples[field] = selfList;
            }
            else
            {
                // 已有字段：把 other 的样本追加到 self 末尾
                int remaining = _maxRows - selfList.Count;
                if (remaining <= 0) break;
                if (otherList.Count <= remaining)
                {
                    selfList.AddRange(otherList);
                }
                else
                {
                    // Reservoir-style 截断：保留 self 已有样本 + 等概率抽样 other 的前 remaining 个
                    selfList.AddRange(otherList.Take(remaining));
                }
            }
        }

        // 旧字段（在 self 中已有但 other 中没有出现）：补 otherSeen 个 null
        foreach (var field in _samples.Keys.ToList())
        {
            if (other._samples.ContainsKey(field)) continue;
            var selfList = _samples[field];
            int remaining = _maxRows - selfList.Count;
            if (remaining <= 0) continue;
            int pad = (int)Math.Min(otherSeen, remaining);
            for (int i = 0; i < pad; i++) selfList.Add(null);
        }

        _seen += otherSeen;
    }
}
