using System.Text;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 超大文件采样模式测试：蓄水池随机抽样（--sample-reservoir）与等距系统抽样（--sample-step）。
/// </summary>
public class SamplingTests
{
    private static IEnumerable<Dictionary<string, object?>> Sequence(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new Dictionary<string, object?> { { "id", (long)i }, { "name", "u" + i } };
        }
    }

    // ──────────────────────────────────────────────
    // 通用采样包装 (Sampling.Apply)
    // ──────────────────────────────────────────────

    [Fact]
    public void Apply_Step_ReturnsEveryNth()
    {
        var sampled = Sampling.Apply(Sequence(1000), reservoir: 0, step: 100).ToList();

        Assert.Equal(10, sampled.Count);
        Assert.Equal(new long[] { 0, 100, 200, 300, 400, 500, 600, 700, 800, 900 },
            sampled.Select(d => (long)d["id"]!));
    }

    [Fact]
    public void Apply_Reservoir_KeepsUpToCapacityFromSource()
    {
        var sampled = Sampling.Apply(Sequence(2000), reservoir: 500, step: 0).ToList();

        Assert.Equal(500, sampled.Count);
        // 样本均来自源集合（无凭空构造）
        var sourceIds = Sequence(2000).Select(d => (long)d["id"]!).ToArray();
        Assert.All(sampled, d => Assert.Contains((long)d["id"]!, sourceIds));
        Assert.Equal(500, sampled.Select(d => (long)d["id"]!).Distinct().Count());
    }

    [Fact]
    public void Apply_Reservoir_LargerThanSource_ReturnsAll()
    {
        var sampled = Sampling.Apply(Sequence(300), reservoir: 500, step: 0).ToList();

        Assert.Equal(300, sampled.Count);
    }

    [Fact]
    public void Apply_Disabled_ReturnsSameSequence()
    {
        var input = Sequence(5).ToList();
        var output = Sampling.Apply(input, reservoir: 0, step: 0).ToList();

        Assert.Equal(input, output);
    }

    // ──────────────────────────────────────────────
    // 参数校验
    // ──────────────────────────────────────────────

    [Fact]
    public void Validate_BothModes_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Sampling.Validate(100, 200));
    }

    [Fact]
    public void Validate_StepOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sampling.Validate(0, 1));
    }

    [Fact]
    public void Validate_ReservoirOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sampling.Validate(1, 0));
    }

    [Fact]
    public void Validate_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sampling.Validate(-1, 0));
    }

    // ──────────────────────────────────────────────
    // 与分析管道集成
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_StepSample_StatsOnlySampledObjects()
    {
        var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(
            Sampling.Apply(Sequence(1000), reservoir: 0, step: 50));

        Assert.Equal(20, result.TotalObjects);
    }

    [Fact]
    public void Analyze_ReservoirSample_StatsOnlySampledObjects()
    {
        var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(
            Sampling.Apply(Sequence(1000), reservoir: 40, step: 0));

        Assert.Equal(40, result.TotalObjects);
    }

    // ──────────────────────────────────────────────
    // JSONL 解析器行/记录级快速路径
    // ──────────────────────────────────────────────

    private static string WriteJsonl(int count, out List<long> ids)
    {
        ids = new List<long>(count);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            ids.Add(i);
            sb.Append("{\"id\":").Append(i).Append(",\"name\":\"u").Append(i).Append("\"}").AppendLine();
        }
        string path = Path.Combine(Path.GetTempPath(), "oa_sample_test_" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void JsonlParser_NoSampling_ParsesAll()
    {
        string path = WriteJsonl(40, out var sourceIds);
        try
        {
            var parsed = FileParsers.ParseByFormat(path, "jsonl", new ParseOptions()).ToList();
            Assert.Equal(40, parsed.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JsonlParser_StepSampling_SkipsUnselectedLines()
    {
        string path = WriteJsonl(200, out var sourceIds);
        try
        {
            var parsed = FileParsers.ParseByFormat(path, "jsonl", new ParseOptions { SampleStep = 10 }).ToList();

            Assert.Equal(20, parsed.Count);
            Assert.Equal(
                sourceIds.Where((_, i) => i % 10 == 0),
                parsed.Select(d => (long)d["id"]!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JsonlParser_ReservoirSampling_ReturnsAtMostCapacity()
    {
        string path = WriteJsonl(300, out var sourceIds);
        try
        {
            var parsed = FileParsers.ParseByFormat(path, "jsonl", new ParseOptions { SampleReservoir = 30 }).ToList();

            Assert.Equal(30, parsed.Count);
            Assert.All(parsed, d => Assert.Contains((long)d["id"]!, sourceIds));
        }
        finally
        {
            File.Delete(path);
        }
    }
}