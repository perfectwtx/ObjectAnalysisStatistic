using MiniExcelLibs;

namespace ObjectAnalyzer.Parsing.Parsers;

internal sealed class ExcelCsvFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "excel", "csv" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".xlsx" };

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        if (options.SampleReservoir > 0)
            return ParseReservoir(stream, options);
        if (options.SampleStep > 0)
            return ParseStep(stream, options);
        return ParseAll(stream, options);
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

    private static IEnumerable<Dictionary<string, object?>> ParseAll(Stream stream, ParseOptions options)
    {
        long rowIndex = 0;
        bool useHeaderRow = UseHeaderRow(options);
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream)))
        {
            rowIndex++;
            if (!useHeaderRow && rowIndex == 1)
                continue; // 表头模式 0：首行是表头，跳过
            var dict = ToRowDict(row);
            if (dict != null)
                yield return dict;
        }
    }

    private static IEnumerable<Dictionary<string, object?>> ParseReservoir(Stream stream, ParseOptions options)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
        bool useHeaderRow = UseHeaderRow(options);
        long rowIndex = 0;
        long dataIndex = 0;
        long skip = Math.Max(0, options.StartFromLine);
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream)))
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
            var dict = ToRowDict(row);
            if (dict != null)
                sampler.SetSlot(slot, dict);
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> ParseStep(Stream stream, ParseOptions options)
    {
        long recordIndex = 0;
        long dataIndex = 0;
        long skip = Math.Max(0, options.StartFromLine);
        bool useHeaderRow = UseHeaderRow(options);
        long rowIndex = 0;
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream)))
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
            var dict = ToRowDict(row);
            if (dict != null)
                yield return dict;
        }
    }

    private static Dictionary<string, object?>? ToRowDict(object? row)
    {
        if (row is not IDictionary<string, object?> rowDict)
            return null;
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in rowDict)
        {
            if (kvp.Key != null)
                dict[kvp.Key] = kvp.Value;
        }
        return dict;
    }
}
