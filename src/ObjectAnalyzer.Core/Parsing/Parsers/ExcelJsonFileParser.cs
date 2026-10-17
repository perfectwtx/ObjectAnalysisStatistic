using System.Collections;
using System.Text.Json;
using MiniExcelLibs;

namespace ObjectAnalyzer.Parsing.Parsers;

internal sealed class ExcelJsonFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "excel-json" };
    public IReadOnlyList<string> SupportedExtensions => Array.Empty<string>(); // 无自动检测扩展名，需在规则或调用方显式使用 excel-json

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        var sources = options.ValueParsers;

        if (options.SampleReservoir > 0)
            return SampleRecords(stream, sources, options);
        if (options.SampleStep > 0)
            return StepRecords(stream, sources, options);
        return EnumerateRecords(stream, sources, options, admitRow: null);
    }

    // ── 表头行语义（每个 source 独立声明，见 ValueParserConfig.Header）──
    // header: 0 → 无表头：source 为列字母（A/B/C），首行即数据；
    // header: N（>=1）→ 第 N 行是该列的表头行：source 为表头标题文本，数据从第 N+1 行开始。
    // 列字母在流式读取到表头行时按标题文本绑定（Trim + 忽略大小写，取最左匹配列）；
    // 找不到标题 / 表头行不存在时显式报错——绝不静默跳过，避免「配置了却不生效」。

    private sealed class SourceState
    {
        public int Index { get; }
        public ValueParserConfig Config { get; }
        public int HeaderRow { get; }
        public bool Bound { get; private set; }
        public string ColumnLetter { get; private set; } = "";

        private SourceState(int index, ValueParserConfig config, int headerRow)
        {
            Index = index;
            Config = config;
            HeaderRow = headerRow;
        }

        public static SourceState Create(int index, ValueParserConfig config)
        {
            int headerRow = config.Header ?? 1;
            if (headerRow < 0)
                throw new InvalidDataException($"valueParsers[{index}] 的 header 必须 >= 0（0=无表头，N=第 N 行是表头）。");
            if (string.IsNullOrWhiteSpace(config.Source))
                throw new InvalidDataException($"valueParsers[{index}] 的 source 不能为空（无表头时为列字母，有表头时为表头标题文本）。");

            var state = new SourceState(index, config, headerRow);
            if (headerRow == 0)
            {
                string letter = config.Source.Trim();
                if (!IsValidColumnLetter(letter))
                    throw new InvalidDataException(
                        $"valueParsers[{index}]: header=0 时 source 必须是列字母（A/B/C…），收到 '{config.Source}'。" +
                        "若要按表头标题定位列，请把 header 设为表头所在行号。");
                state.Bind(letter); // 无表头：source 即列字母
            }
            return state;
        }

        private void Bind(string columnLetter)
        {
            ColumnLetter = columnLetter;
            Bound = true;
        }

        public void BindFromHeaderRow(IDictionary<string, object?> headerRowDict)
        {
            string title = Config.Source.Trim();
            foreach (var kvp in headerRowDict)
            {
                if (kvp.Value is null)
                    continue;
                if (string.Equals(kvp.Value.ToString()?.Trim(), title, StringComparison.OrdinalIgnoreCase))
                {
                    Bind(kvp.Key);
                    return;
                }
            }

            throw new InvalidDataException($"valueParsers[{Index}]: 第 {HeaderRow} 行找不到标题为 '{title}' 的列。");
        }
    }

    // 列字母校验：单字母任意大小写（A/a）；多字母须全大写（AB/XFD）——
    // 混合大小写（如 "Age"）几乎肯定是把表头标题写进了 header=0 的 source，报错并给出提示
    private static bool IsValidColumnLetter(string value) =>
        value.Length switch
        {
            1 => char.IsAsciiLetter(value[0]),
            >= 2 and <= 3 => value.All(c => c is >= 'A' and <= 'Z'),
            _ => false
        };

    private static IEnumerable<Dictionary<string, object?>> EnumerateRecords(
        Stream stream,
        IReadOnlyList<ValueParserConfig> sources,
        ParseOptions options,
        Func<long, bool>? admitRow)
    {
        var states = sources.Select((config, index) => SourceState.Create(index, config)).ToList();

        long rowIndex = 0;
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: false, excelType: ParserHelpers.ResolveExcelType(stream)))
        {
            rowIndex++;
            if (row is not IDictionary<string, object?> rowDict)
                continue;

            // 表头行恰好到达：先绑定（在采样准入之前——表头行被采样跳过也必须完成绑定）
            foreach (var state in states)
            {
                if (!state.Bound && state.HeaderRow == rowIndex)
                    state.BindFromHeaderRow(rowDict);
            }

            if (admitRow is not null && !admitRow(rowIndex))
                continue; // 未选中行：连 JSON 单元格都不解析

            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                if (!state.Bound || rowIndex <= state.HeaderRow)
                    continue; // 表头行之前：该列尚未开始出数

                string sourceKey = state.Config.Source.Trim();
                object? cellValue = LookupCellValue(rowDict, state.ColumnLetter);
                if (cellValue == null)
                    continue;

                var parsedValue = ParseValueByType(cellValue, state.Config.ParseType, sourceKey, rowIndex, options);
                if (parsedValue == null)
                    continue;

                if (state.Config.Flatten ?? options.Flatten)
                    AddFlattenedParsedValue(record, sourceKey, parsedValue);
                else
                    record[FieldPath.Property(sourceKey).ToString()] = parsedValue;
            }

            if (record.Count > 0)
                yield return record;
        }

        var unbound = states.FirstOrDefault(s => !s.Bound);
        if (unbound is not null)
            throw new InvalidDataException(
                $"valueParsers[{unbound.Index}]: 工作表中不存在第 {unbound.HeaderRow} 行，找不到标题为 '{unbound.Config.Source.Trim()}' 的列。");
    }

    // 采样：准入判定在记录构建之前，未选中行不解析 JSON 单元格
    private static IEnumerable<Dictionary<string, object?>> SampleRecords(
        Stream stream, IReadOnlyList<ValueParserConfig> sources, ParseOptions options)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
        int slot = -1;
        foreach (var record in EnumerateRecords(stream, sources, options,
                     admitRow: _ => { slot = sampler.NextSlot(); return slot >= 0; }))
        {
            sampler.SetSlot(slot, record);
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> StepRecords(
        Stream stream, IReadOnlyList<ValueParserConfig> sources, ParseOptions options)
    {
        return EnumerateRecords(stream, sources, options, admitRow: rowIndex => (rowIndex - 1) % options.SampleStep == 0);
    }

    private static void AddFlattenedParsedValue(Dictionary<string, object?> result, string sourceKey, object? parsedValue)
    {
        ParserHelpers.FlattenValue(RecordValue.FromObject(parsedValue), FieldPath.Property(sourceKey), result);
    }

    private static object? ParseValueByType(object? value, string parseType, string sourceKey, long rowIndex, ParseOptions options)
    {
        string raw = value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalizedType = parseType?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedType))
            normalizedType = "auto";

        normalizedType = normalizedType.ToLowerInvariant();
        return normalizedType switch
        {
            "none" => raw,
            "auto" => TryParseJsonValue(raw, out var autoValue) ? autoValue : raw,
            "json" => ParseJsonValue(raw, sourceKey, rowIndex),
            "json-string" => ParseJsonStringValue(raw, sourceKey, rowIndex),
            _ => throw new InvalidDataException($"不支持的 parseType '{parseType}'（支持: none / auto / json / json-string）。")
        };
    }

    private static object? ParseJsonStringValue(string jsonString, string sourceKey, long rowIndex)
    {
        var trimmed = jsonString.Trim();
        if (trimmed.Length == 0)
            return null;

        if (TryParseJsonValue(trimmed, out var directValue))
            return directValue;

        if (trimmed.StartsWith('"'))
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"第 {rowIndex} 行 (源 {sourceKey}) 的 json-string 值必须是一个 JSON 字符串。");
            var inner = root.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(inner))
                return "";
            return TryParseJsonValue(inner, out var nested) ? nested : inner;
        }

        return ParseJsonValue(trimmed, sourceKey, rowIndex);
    }

    private static object? ParseJsonValue(string jsonString, string sourceKey, long rowIndex)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return null;

        if (!TryParseJsonValue(jsonString, out var parsed))
            throw new InvalidDataException($"第 {rowIndex} 行 (源 {sourceKey}) 的值不是有效 JSON：{jsonString}");
        return parsed;
    }

    private static bool TryParseJsonValue(string raw, out object? value)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            value = null;
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            value = ConvertJsonDocument(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    private static object? ConvertJsonDocument(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParserHelpers.ParseJsonElement(element, true),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonDocument).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.TryGetDouble(out var d) ? d : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static object? LookupCellValue(IDictionary<string, object?> rowDict, string sourceKey)
    {
        if (rowDict.TryGetValue(sourceKey, out var exact))
            return exact;

        foreach (var kvp in rowDict)
        {
            if (string.Equals(kvp.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }
}
