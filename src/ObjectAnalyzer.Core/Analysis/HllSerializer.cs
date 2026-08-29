using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>HyperLogLog 序列化辅助：稠密寄存器 → base64，便于 JSON 存储。</summary>
public static class HllSerializer
{
    public static string? ToBase64(HyperLogLog hll)
    {
        var dense = GetDenseRegisters(hll);
        if (dense == null) return null;
        return Convert.ToBase64String(dense);
    }

    public static int? GetPrecision(HyperLogLog hll)
    {
        var dense = GetDenseRegisters(hll);
        if (dense == null) return null;
        return (int)Math.Log2(dense.Length);
    }

    public static HyperLogLog FromBase64(string base64, int precision)
    {
        var bytes = Convert.FromBase64String(base64);
        var hll = new HyperLogLog(precision);
        SetDenseRegisters(hll, bytes);
        return hll;
    }

    private static byte[]? GetDenseRegisters(HyperLogLog hll)
    {
        var field = typeof(HyperLogLog).GetField("_dense",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(hll) as byte[];
    }

    private static void SetDenseRegisters(HyperLogLog hll, byte[] bytes)
    {
        var field = typeof(HyperLogLog).GetField("_dense",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(hll, bytes);
    }

    /// <summary>两个 HLL 之间的 Jaccard 相似度估算（基于寄存器的位级 Jaccard）。</summary>
    public static double? JaccardApprox(HyperLogLog a, HyperLogLog b)
    {
        var ra = GetDenseRegisters(a);
        var rb = GetDenseRegisters(b);
        if (ra == null || rb == null || ra.Length != rb.Length) return null;
        int inter = 0, union = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            byte max = Math.Max(ra[i], rb[i]);
            byte min = Math.Min(ra[i], rb[i]);
            if (max > 0) union++;
            if (min > 0) inter++;
        }
        return union == 0 ? 1.0 : (double)inter / union;
    }
}
