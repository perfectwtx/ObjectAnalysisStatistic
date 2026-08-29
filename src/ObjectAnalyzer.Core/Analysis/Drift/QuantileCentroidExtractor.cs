namespace ObjectAnalyzer.Analysis;

/// <summary>从 <see cref="MergeableQuantileSketch"/> 提取质心序列（公开便于测试）。</summary>
public static class QuantileCentroidExtractor
{
    public static List<QuantileCentroid>? Extract(MergeableQuantileSketch sketch)
    {
        if (sketch == null) return null;
        // 通过反射读取私有 _centroids 字段（简单且不暴露内部 API）。
        var field = typeof(MergeableQuantileSketch).GetField("_centroids",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) return null;
        var centroids = field.GetValue(sketch) as System.Collections.IEnumerable;
        if (centroids == null) return null;

        var list = new List<QuantileCentroid>();
        foreach (var c in centroids)
        {
            // c 是 (double Mean, long Weight) 的 record struct
            var t = c.GetType();
            var mean = (double)t.GetProperty("Mean")!.GetValue(c)!;
            var weight = (long)t.GetProperty("Weight")!.GetValue(c)!;
            list.Add(new QuantileCentroid { Mean = mean, Weight = weight });
        }
        return list;
    }

    /// <summary>从质心数组重建 t-digest 草图（用于分布对比）。</summary>
    public static MergeableQuantileSketch Rehydrate(List<QuantileCentroid> centroids, int capacity = 256)
    {
        var sketch = new MergeableQuantileSketch(capacity);
        foreach (var c in centroids)
        {
            // 不区分 double/long，直接 Add
            sketch.Add(c.Mean, c.Weight);
        }
        return sketch;
    }
}
