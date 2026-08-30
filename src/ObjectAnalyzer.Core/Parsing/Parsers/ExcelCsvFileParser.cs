using MiniExcelLibs;

namespace ObjectAnalyzer.Parsing.Parsers;

internal sealed class ExcelCsvFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "excel", "csv" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".xlsx" };

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        // MiniExcel 无法从流推断类型，这里解析一次并透传：
        // 既省掉每个分支重复探测，也让下面的分支知道要不要做 CSV 数值推断
        var excelType = ParserHelpers.ResolveExcelType(stream);

        if (options.SampleReservoir > 0)
            return ParseReservoir(stream, options, excelType);
        if (options.SampleStep > 0)
            return ParseStep(stream, options, excelType);
        return ParseAll(stream, options, excelType);
    }

    // 表头模式：header=0 无表头（列以字母 A/B/C 命名，首行是表头需跳过）；header=1（默认）首行值为列名。
    private static bool UseHeaderRow(ParseOptions options)
    {
        foreach (var source in options.ValueParsers)
        {
            if (source.Header is int header)
                return header == 1;
        }
        return true;
    }

    private static IEnumerable<Dictionary<string, object?>> ParseAll(
        Stream stream, ParseOptions options, ExcelType excelType)
    {
        long rowIndex = 0;
        bool useHeaderRow = UseHeaderRow(options);
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: excelType))
        {
            rowIndex++;
            if (!useHeaderRow && rowIndex == 1)
                continue; // 表头模式 0：首行是表头，跳过
            var dict = ToRowDict(row, excelType, options.CsvInferNumbers);
            if (dict != null)
                yield return dict;
        }
    }

    private static IEnumerable<Dictionary<string, object?>> ParseReservoir(
        Stream stream, ParseOptions options, ExcelType excelType)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
        bool useHeaderRow = UseHeaderRow(options);
        long rowIndex = 0;
        long dataIndex = 0;
        long skip = Math.Max(0, options.StartFromLine);
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: excelType))
        {
            rowIndex++;
            if (!useHeaderRow && rowIndex == 1)
                continue;
            if (dataIndex < skip)
            {
                dataIndex++;
                continue; // P6.2：跳过前 StartFromLine 行
            }
            dataIndex++;
            int slot = sampler.NextSlot();
            if (slot < 0)
                continue;
            var dict = ToRowDict(row, excelType, options.CsvInferNumbers);
            if (dict != null)
                sampler.SetSlot(slot, dict);
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> ParseStep(
        Stream stream, ParseOptions options, ExcelType excelType)
    {
        long recordIndex = 0;
        long dataIndex = 0;
        long skip = Math.Max(0, options.StartFromLine);
        bool useHeaderRow = UseHeaderRow(options);
        long rowIndex = 0;
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: excelType))
        {
            rowIndex++;
            if (!useHeaderRow && rowIndex == 1)
                continue;
            if (dataIndex < skip)
            {
                dataIndex++;
                continue; // P6.2：跳过前 StartFromLine 行
            }
            dataIndex++;
            if (recordIndex++ % options.SampleStep != 0)
                continue;
            var dict = ToRowDict(row, excelType, options.CsvInferNumbers);
            if (dict != null)
                yield return dict;
        }
    }

    /// <param name="inferNumbers">是否对 CSV 的字符串值做数值推断（见 <see cref="ParseOptions.CsvInferNumbers"/>）。</param>
    private static Dictionary<string, object?>? ToRowDict(object? row, ExcelType excelType, bool inferNumbers)
    {
        if (row is not IDictionary<string, object?> rowDict)
            return null;

        // 只推断 CSV：xlsx 的单元格值由 MiniExcel 按单元格类型给出（数值/日期/文本），
        // 已经是强类型的，再猜一次反而会把「存成文本的编码」转坏
        bool convert = inferNumbers && excelType == ExcelType.CSV;

        var dict = new Dictionary<string, object?>();
        foreach (var kvp in rowDict)
        {
            if (kvp.Key != null)
                dict[kvp.Key] = convert ? CsvNumberInference.TryConvert(kvp.Value) : kvp.Value;
        }
        return dict;
    }
}
