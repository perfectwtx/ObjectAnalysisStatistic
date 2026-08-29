using System.Text.Json;

namespace ObjectAnalyzer.Parsing.Parsers;

internal sealed class JsonlFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "jsonl" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".jsonl", ".ndjson" };

    // 惰性逐行流式解析：超大 JSONL 文件无需一次性物化到内存。
    // 采样模式下（--sample-reservoir / --sample-step）仅在选中行上做 JSON 解析，
    // 未选中行直接跳过，从而在巨型文件上数秒内完成 Schema 概览。
    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        if (options.SampleReservoir > 0)
            return ParseReservoir(stream, options);
        if (options.SampleStep > 0)
            return ParseStep(stream, options);
        return ParseAll(stream, options);
    }

    private static IEnumerable<Dictionary<string, object?>> ParseAll(Stream stream, ParseOptions options)
    {
        using var reader = new StreamReader(stream);
        string? line;
        long lineNumber = 0;
        // P6.2 断点续跑：跳过前 StartFromLine 行后开始 yield
        long linesToSkip = Math.Max(0, options.StartFromLine);
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (lineNumber <= linesToSkip) continue;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            yield return ParseJsonLine(line, lineNumber, options);
        }
    }

    private static IEnumerable<Dictionary<string, object?>> ParseReservoir(Stream stream, ParseOptions options)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
        using var reader = new StreamReader(stream);
        string? line;
        long lineNumber = 0;
        long linesToSkip = Math.Max(0, options.StartFromLine);
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (lineNumber <= linesToSkip) continue;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int slot = sampler.NextSlot();
            if (slot < 0)
                continue; // 未命中槽位：跳过解析，只付出 I/O 成本
            sampler.SetSlot(slot, ParseJsonLine(line, lineNumber, options));
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> ParseStep(Stream stream, ParseOptions options)
    {
        using var reader = new StreamReader(stream);
        string? line;
        long lineNumber = 0;
        long recordIndex = 0;
        long linesToSkip = Math.Max(0, options.StartFromLine);
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (lineNumber <= linesToSkip) continue;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (recordIndex++ % options.SampleStep != 0)
                continue; // 未命中步长：跳过解析
            yield return ParseJsonLine(line, lineNumber, options);
        }
    }

    private static Dictionary<string, object?> ParseJsonLine(string line, long lineNumber, ParseOptions options)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"在第 {lineNumber} 行解析 JSON 时出错: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"第 {lineNumber} 行的 JSON 根节点必须是一个对象。");
            return ParserHelpers.ParseJsonElement(root, options.Flatten);
        }
    }
}
