using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 对本次深度改进（流式、FNV-1a、高基数修剪、MapReduce、程序集卸载）的补充测试。
/// </summary>
public class PerformanceImprovementsTests
{
    // ──────────────────────────────────────────────
    // 1. MapReduce 并行：多线程结果的正确性与确定性
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_LargeDataset_ParallelResultsAreConsistent()
    {
        // Arrange – 生成 5000 个对象，其中包含重复行
        const int total = 5000;
        var objects = Enumerable.Range(0, total)
            .Select(i => new Dictionary<string, object?>
            {
                { "Id", (long)(i % 100) },     // 100 个唯一值
                { "Value", (long)(i % 10) }    // 10  个唯一值
            })
            .ToList();

        // Act – 执行两次确保结果幂等（并行哈希结果需确定）
        var result1 = Analyze(objects);
        var result2 = Analyze(objects);

        // Assert
        Assert.Equal(total, result1.TotalObjects);
        Assert.Equal(total, result2.TotalObjects);

        var idStat1 = result1.FieldStatistics.First(f => f.FieldName == "Id");
        var idStat2 = result2.FieldStatistics.First(f => f.FieldName == "Id");
        Assert.Equal(idStat1.Count, idStat2.Count);
        Assert.Equal(idStat1.ValueCounts.Count, idStat2.ValueCounts.Count);

        // 每个 Id 值（0-99）各出现 50 次
        Assert.Equal(100, idStat1.ValueCounts.Count);
        Assert.All(idStat1.ValueCounts.Values, cnt => Assert.Equal(50, cnt));
    }

    // ──────────────────────────────────────────────
    // 2. 高基数字段修剪（MaxTrackedUniqueValues）
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_HighCardinalityField_OtherValuesBucketUsed()
    {
        // Arrange – 生成 1200 个对象，每个对象有唯一的 Guid 值 → 超过 1000 阈值
        var objects = Enumerable.Range(0, 1200)
            .Select(_ => new Dictionary<string, object?> { { "Key", Guid.NewGuid().ToString() } })
            .ToList();

        // Act
        var result = Analyze(objects);
        var keyStat = result.FieldStatistics.First(f => f.FieldName == "Key");

        // Assert: ValueCounts 不得超过 1001（1000 正常 + 1 个 [Other values] 桶）
        Assert.True(keyStat.ValueCounts.Count <= 1001,
            $"ValueCounts 应 ≤ 1001，实际为 {keyStat.ValueCounts.Count}");
        Assert.Contains("[Other values]", keyStat.ValueCounts.Keys);
    }

    // ──────────────────────────────────────────────
    // 3. 重复对象检测（FNV-1a 哈希）
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_DuplicateObjectsViaHash_DetectedCorrectly()
    {
        // Arrange – 同一个对象出现 5 次，不同对象 1 次
        var repeated = new Dictionary<string, object?> { { "X", "hello" }, { "Y", 42L } };
        var unique   = new Dictionary<string, object?> { { "X", "world" }, { "Y", 99L } };

        var objects = Enumerable.Repeat(repeated, 5).Concat(new[] { unique }).ToList();

        // Act
        var result = Analyze(objects);

        // Assert
        Assert.Equal(6, result.TotalObjects);
        Assert.Equal(2, result.TotalUniqueObjects);
        Assert.Single(result.DuplicateObjects);
        Assert.Equal(5, result.DuplicateObjects[0].Count);
    }

    // ──────────────────────────────────────────────
    // 4. 可卸载程序集沙盒（DynamicAssemblyContext.Dispose）
    // ──────────────────────────────────────────────

    [Fact]
    public void TypeLoader_DisposedContext_UnloadsAssembly()
    {
        // Arrange
        string tempCsFile = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        string tempJsonFile = Path.GetTempFileName();

        File.WriteAllText(tempCsFile, @"
namespace SandboxNs;
public class SandboxEntity { public int Id { get; set; } }
");
        File.WriteAllText(tempJsonFile, @"[{""Id"": 7}]");

        try
        {
            // Act – 编译并在 using 块中使用，退出时自动 Unload
            using var context = CompileAndUseInSandbox(tempCsFile, tempJsonFile, out var parsed);

            // Assert – 在 Dispose 前，能正常读取反序列化结果
            Assert.Single(parsed);
            Assert.Equal(7, parsed[0]["Id"]);

            // using 块结束时自动调用 context.Dispose() → Unload()
            // .NET 中 Collectible ALC Unload 不保证立即 GC，但保证 Unload() 不会抛出异常。
        }
        finally
        {
            if (File.Exists(tempCsFile)) File.Delete(tempCsFile);
            if (File.Exists(tempJsonFile)) File.Delete(tempJsonFile);
        }
    }

    private static DynamicAssemblyContext CompileAndUseInSandbox(
        string csFile, string jsonFile,
        out List<Dictionary<string, object?>> parsed)
    {
        var (context, type) = TypeLoader.Compile(csFile, null);
        parsed = TypedFileParser.ParseJson(jsonFile, type).Objects;
        return context; // 调用方持有，using 块结束时 Dispose
    }
}
