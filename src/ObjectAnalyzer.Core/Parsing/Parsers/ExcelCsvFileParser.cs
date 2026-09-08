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
            return SampleRecords(stream, options, excelType);
        if (options.SampleStep > 0)
            return StepRecords(stream, options, excelType);
        return EnumerateDataRows(stream, options, excelType, admitRow: null);
    }

    // ── 表头行语义（整表共用一个行号，取 valueParsers 里首个声明的值，默认 1）──
    // 1（默认）：首行为表头，列键 = 首行文本；
    // N（>=2）：第 N 行为表头，跳过之前的行，数据行列键重写为第 N 行文本（空文本列回退列字母）；
    // 0：无表头，首行即数据，列键 = 列字母（A/B/C）。
    // 说明：valueParsers 驱动的 excel-json 专用解析支持「每个 source 独立声明表头行」；
    // 通用 excel/csv 读取里表头是整表概念，多个 source 声明不一致时取第一个声明的值。
    private static int ResolveHeaderRow(ParseOptions options)
    {
        foreach (var source in options.ValueParsers)
        {
            if (source.Header is int header)
            {
                if (header < 0)
                    throw new InvalidDataException("valueParsers 的 header 必须 >= 0（0=无表头，N=第 N 行是表头）。");
                return header;
            }
        }
        return 1;
    }

    private static IEnumerable<Dictionary<string, object?>> EnumerateDataRows(
        Stream stream, ParseOptions options, ExcelType excelType, Func<long, bool>? admitRow)
    {
        int headerRow = ResolveHeaderRow(options);

        if (headerRow == 1)
        {
            long dataIndex = 0;
            long skip = Math.Max(0, options.StartFromLine);
            long rowIndex = 0;
            foreach (var row in MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType))
            {
                rowIndex++;
                if (dataIndex < skip)
                {
                    dataIndex++;
                    continue; // P6.2：跳过前 StartFromLine 行
                }
                if (admitRow is not null && !admitRow(rowIndex))
                    continue;
                var dict = ToRowDict(row, excelType, options.CsvInferNumbers);
                if (dict != null)
                    yield return dict;
            }
            yield break;
        }

        // header=0：无表头，首行即数据；header>=2：跳到表头行，其后数据行键重写为表头文本
        Dictionary<string, string>? headerNames = null;
        bool headerCaptured = false;
        long rowIdx = 0;
        long dataSkipped = 0;
        long skipCount = Math.Max(0, options.StartFromLine);
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: false, excelType: excelType))
        {
            rowIdx++;
            if (row is not IDictionary<string, object?> rowDict)
                continue;

            if (headerRow >= 2 && !headerCaptured)
            {
                if (rowIdx < headerRow)
                    continue;
                headerNames = CaptureHeaderNames(rowDict);
                headerCaptured = true;
                continue; // 表头行本身不是数据
            }

            if (dataSkipped < skipCount)
            {
                dataSkipped++;
                continue; // P6.2：跳过前 StartFromLine 行
            }

            if (admitRow is not null && !admitRow(rowIdx))
                continue;

            var data = ToRowDict(RenameRowKeys(rowDict, headerNames), excelType, options.CsvInferNumbers);
            if (data != null)
                yield return data;
        }
    }

    private static Dictionary<string, string> CaptureHeaderNames(IDictionary<string, object?> rowDict) =>
        rowDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "", StringComparer.Ordinal);

    private static IDictionary<string, object?> RenameRowKeys(
        IDictionary<string, object?> rowDict, Dictionary<string, string>? headerNames)
    {
        if (headerNames is null || headerNames.Count == 0)
            return rowDict;

        // 表头文本重复时后者覆盖前者（字典语义）；空表头单元格回退列字母
        var renamed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in rowDict)
        {
            renamed[headerNames.TryGetValue(kvp.Key, out var name) && name.Length > 0 ? name : kvp.Key] = kvp.Value;
        }
        return renamed;
    }

    // 采样：准入判定在行字典构建之前
    private static IEnumerable<Dictionary<string, object?>> SampleRecords(
        Stream stream, ParseOptions options, ExcelType excelType)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
        int slot = -1;
        foreach (var dict in EnumerateDataRows(stream, options, excelType,
                     admitRow: _ => { slot = sampler.NextSlot(); return slot >= 0; }))
        {
            sampler.SetSlot(slot, dict);
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> StepRecords(
        Stream stream, ParseOptions options, ExcelType excelType)
    {
        return EnumerateDataRows(stream, options, excelType, admitRow: rowIndex => (rowIndex - 1) % options.SampleStep == 0);
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
