using System.Text.Json;

namespace ObjectAnalyzer.Parsing.Parsers;

// ─────────────────────────────────────────────
//  Individual format parsers (IFileParser)
// ─────────────────────────────────────────────

internal sealed class JsonFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "json" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".json" };

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        // 流式解析：每个元素独立 JsonDocument，生命周期很短，
        // 避免整个 JSON DOM 与分析过程同时常驻内存。
        byte[] fileBytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            fileBytes = ms.ToArray();
        }
        // 剥离 UTF-8 BOM，避免 Utf8JsonReader 将 BOM 当作非法字节
        // （Windows 工具/Excel 保存的 JSON 常带 BOM）
        fileBytes = StripUtf8Bom(fileBytes);
        foreach (var item in ParseFromBytes(fileBytes, options))
            yield return item;
    }

    private static byte[] StripUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsSpan(3).ToArray();
        }
        return bytes;
    }

    private static IEnumerable<Dictionary<string, object?>> ParseFromBytes(byte[] fileBytes, ParseOptions options)
    {
        JsonTokenType rootToken = PeekRootToken(fileBytes);

        if (rootToken == JsonTokenType.StartArray)
        {
            // 先消费根 '[',否则 TryParseValue 会把整个数组当作一个元素
            long offset = ConsumeSingleToken(fileBytes);
            // 空数组也合法（产出 0 个对象）
            long skip = Math.Max(0, options.StartFromLine);
            long yielded = 0;
            while (TryReadNextElement(fileBytes, ref offset, out var elementDoc))
            {
                if (yielded < skip)
                {
                    yielded++;
                    elementDoc.Dispose();
                    continue; // P6.2：跳过前 StartFromLine 个数组元素
                }
                yielded++;
                using (elementDoc)
                {
                    yield return ParserHelpers.ParseJsonElement(elementDoc.RootElement, options.Flatten);
                }
            }
        }
        else if (rootToken == JsonTokenType.StartObject)
        {
            long offset = 0;
            if (!TryReadNextElement(fileBytes, ref offset, out var singleDoc))
                throw new InvalidDataException("JSON 根节点必须是数组或对象。");
            using (singleDoc)
            {
                yield return ParserHelpers.ParseJsonElement(singleDoc.RootElement, options.Flatten);
            }
        }
        else
        {
            throw new InvalidDataException("JSON 根节点必须是数组或对象。");
        }
    }

    private static JsonTokenType PeekRootToken(byte[] fileBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(fileBytes);
            while (reader.Read())
            {
                return reader.TokenType;
            }
            return JsonTokenType.None;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
        }
    }

    /// <summary>
    /// 从 offset 处读取下一个顶层 JSON 值（跳过空白与逗号）。
    /// 返回 false 表示遇到数组/对象结束符。每个元素的 reader 都是局部的，
    /// 避免 ref struct 跨 yield 边界。
    /// </summary>
    /// <summary>
    /// 从开头读取一个顶层 token（如根 '['）并返回其结束偏移。
    /// </summary>
    private static long ConsumeSingleToken(byte[] fileBytes)
    {
        var reader = new Utf8JsonReader(fileBytes);
        try
        {
            if (!reader.Read())
                throw new InvalidDataException("JSON 文件为空，缺少根 token。");
            return reader.BytesConsumed;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
        }
    }

    private static bool TryReadNextElement(byte[] fileBytes, ref long offset, out JsonDocument doc)
    {
        // 跳过元素间的分隔逗号与空白（TryParseValue 的 BytesConsumed 停在逗号前）
        while (offset < fileBytes.Length)
        {
            byte b = fileBytes[offset];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)',')
                offset++;
            else
                break;
        }

        if (offset >= fileBytes.Length || fileBytes[offset] == (byte)']' || fileBytes[offset] == (byte)'}')
        {
            doc = null!;
            return false;
        }

        var reader = new Utf8JsonReader(fileBytes.AsSpan((int)offset));
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.EndArray:
                    case JsonTokenType.EndObject:
                        offset += reader.BytesConsumed;
                        doc = null!;
                        return false;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        if (JsonDocument.TryParseValue(ref reader, out var parsed))
                        {
                            offset += reader.BytesConsumed;
                            doc = parsed;
                            return true;
                        }
                        throw new InvalidDataException("JSON 值解析失败。");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
        }

        offset = fileBytes.Length;
        doc = null!;
        return false;
    }
}
