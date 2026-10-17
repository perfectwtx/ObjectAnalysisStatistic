using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>
/// 统一分析管线（W1）：解析 → JSONPath 投影 → 字段选择/转换/过滤 → 采样 → 统计。
/// CLI / API / Web 三宿主共用同一条计算路径；输入为 <see cref="AnalysisOptions"/>（IR）+
/// <see cref="AnalysisRules"/>，输出 <see cref="PipelineResult"/>。呈现/导出/门禁属于宿主职责，不在管线内。
/// </summary>
public static class AnalysisPipeline
{
    /// <summary>
    /// 执行分析管线，并向宿主暴露统一的生命周期事件。
    /// </summary>
    public static PipelineResult Execute(
        AnalysisOptions options,
        AnalysisRules? rules,
        AnalysisRunContext? runContext,
        CancellationToken cancellationToken = default)
    {
        var ctx = runContext ?? new AnalysisRunContext();
        ctx.ExecutionObserver?.OnStarted();

        try
        {
            var result = ExecuteCore(options, rules, ctx, cancellationToken);
            ctx.ExecutionObserver?.OnCompleted(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ctx.ExecutionObserver?.OnCancelled();
            throw;
        }
        catch (Exception ex)
        {
            ctx.ExecutionObserver?.OnFailed(ex);
            throw;
        }
    }

    /// <summary>
    /// 原始分析实现。生命周期管理由 Execute 统一负责，避免污染具体分析步骤。
    /// </summary>
    private static PipelineResult ExecuteCore(
        AnalysisOptions options,
        AnalysisRules? rules,
        AnalysisRunContext ctx,
        CancellationToken cancellationToken)
    {
        // 1. 解析分发：数据库直连 / root-path 整体投影 / 常规文件
        IEnumerable<Dictionary<string, object?>> objects = ParseDispatch(options, rules, ctx);

        // 2. record-path 投影：每条记录可能 fan-out，始终生效（即使在 root-path 之后）
        if (!string.IsNullOrWhiteSpace(options.Input.RecordPath))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[JSONPath]][/] 正在每条记录上应用 [blue]{MarkupText.Escape(options.Input.RecordPath!)}[/] ...");
            objects = ApplyRecordPathStream(objects, options.Input.RecordPath!);
        }

        // 3. 字段选择 + 值转换 + 过滤（--fields / runtime.selectedFields / transform / --where / runtime.filter）
        var transformProcessor = new FieldTransformProcessor(rules, SplitFields(options.FieldSelection.Fields), options.FieldSelection.Filter);
        if (options.FieldSelection.Filter != null)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[过滤]][/] 已应用过滤条件: [blue]{MarkupText.Escape(options.FieldSelection.Filter)}[/]");
        }
        if (transformProcessor.HasSelectionOrTransforms)
        {
            objects = transformProcessor.Wrap(objects);
        }

        // 4. 采样（仅整体物化格式：jsonl / csv / excel 已在解析器内按行/记录跳过，见 ParseDispatch）
        objects = ApplySamplingIfEnabled(objects, options, ctx);

        // 5. 数据预览捕获（Web）：在进入统计前捕获前 N 条（转换/采样后）
        List<Dictionary<string, object?>>? preview = null;
        if (ctx.PreviewSink != null)
        {
            preview = new List<Dictionary<string, object?>>(Math.Max(0, ctx.PreviewLimit));
            objects = WrapWithPreview(objects, preview, ctx.PreviewLimit);
        }

        // 6. 关系跟踪：与统计共用同一次枚举，记录嵌套对象/对象集合关系，避免再次读取输入。
        IReadOnlyList<ObjectRelationship> objectRelationships = Array.Empty<ObjectRelationship>();
        objects = ObjectRelationshipAnalyzer.Track(objects, relationships => objectRelationships = relationships);

        // 7. 物化判定：模糊去重需要随机访问；分布草图入口 AnalyzeDetailed 内部亦需多遍
        bool needFuzzyDedup = !string.IsNullOrWhiteSpace(options.Advanced.FuzzyDedupField);
        bool needDistributionSketches =
            !string.IsNullOrEmpty(options.Distribution.DistributionSnapshotPath) ||
            !string.IsNullOrEmpty(options.Distribution.DiffDistributionBaseline);
        List<Dictionary<string, object?>> materialized = (needFuzzyDedup || needDistributionSketches)
            ? objects.ToList()
            : new List<Dictionary<string, object?>>();

        // 8. 模糊去重（--fuzzy-dedup-field）
        FuzzyDuplicateReport? fuzzyReport = null;
        if (needFuzzyDedup)
        {
            string field = options.Advanced.FuzzyDedupField!;
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[模糊去重]][/] 正在对字段 [blue]{MarkupText.Escape(field)}[/] 做形态变体聚类...");
            fuzzyReport = FuzzyDuplicateDetector.Detect(materialized, obj =>
            {
                if (obj.TryGetValue(field, out var v)) return v?.ToString();
                return null;
            });
        }

        // 9. 统计：进度端口包装（CLI：Spectre 进度条；API/Web：直接执行）
        AnalysisResult result = null!;
        Dictionary<string, MergeableQuantileSketch>? quantileSketches = null;
        Dictionary<string, HyperLogLog>? distinctSketches = null;

        long? expectedTotal = options.Progress.ShowProgress ? EstimateTotalObjects(materialized) : null;
        string description = string.IsNullOrEmpty(options.Input.InputPath)
            ? "分析中（数据库直连）"
            : $"分析中 {Path.GetFileName(options.Input.InputPath)}";

        AnalysisServices.Current.Progress.Run(description, expectedTotal, options.Progress.ShowProgress, uiProgress =>
        {
            IProgress<long>? progress = BuildProgressBridge(uiProgress, ctx);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Parse.MaxDegreeOfParallelism > 0
                    ? options.Parse.MaxDegreeOfParallelism
                    : -1
            };
            if (needDistributionSketches)
            {
                var detailed = ObjectAnalyzer.AnalyzeDetailed(
                    materialized, rules, parallelOptions, options.Advanced, progress,
                    options.Parse.MaxTrackedValues, cancellationToken);
                result = detailed.Result;
                quantileSketches = detailed.QuantileSketches;
                distinctSketches = detailed.DistinctSketches;
            }
            else
            {
                result = ObjectAnalyzer.Analyze(
                    objects, rules, parallelOptions, options.Advanced, progress,
                    options.Parse.MaxTrackedValues, cancellationToken);
            }
        });

        // 10. 回填与补充统计
        if (fuzzyReport != null) result.FuzzyDuplicates = fuzzyReport;
        result.ObjectRelationships = objectRelationships.ToList();
        transformProcessor.ApplyFailures(result);
        result.FilterExpression = options.FieldSelection.Filter;
        BuildSamplingConfidence(result, options);

        // 11. 预览回调（统计成功完成后一次性交付）
        ctx.PreviewSink?.Invoke(preview ?? new List<Dictionary<string, object?>>());

        return new PipelineResult
        {
            Result = result,
            QuantileSketches = quantileSketches,
            DistinctSketches = distinctSketches
        };
    }

    // ── 解析分发 ──

    private static IEnumerable<Dictionary<string, object?>> ParseDispatch(
        AnalysisOptions options, AnalysisRules? rules, AnalysisRunContext ctx)
    {
        bool dbMode = !string.IsNullOrEmpty(options.Input.ConnectionString);
        var valueParsers = ResolveValueParsers(rules);

        if (dbMode)
        {
            return ParseFromDatabase(options, valueParsers, ctx);
        }

        bool hasRootPath = !string.IsNullOrWhiteSpace(options.Input.RootPath);
        if (hasRootPath)
        {
            string fmt = ResolveEffectiveFormat(options.Input.InputPath, rules, options.Input.Format);
            if (fmt is "jsonl" or "ndjson" or "csv" or "excel" or "excel-json" or "db")
            {
                AnalysisServices.Current.Console.MarkupLine(
                    $"[yellow]警告: --root-path 在 [red]{fmt}[/] 格式下没有文件根概念，将被忽略（仅 --record-path 生效）[/]");
            }
            else
            {
                AnalysisServices.Current.Console.MarkupLine(
                    $"[grey][[JSONPath]][/] 正在文件根应用 [blue]{MarkupText.Escape(options.Input.RootPath!)}[/] ...");
                ctx.EffectiveFormat = fmt;
                return LoadAndApplyRootPath(options.Input.InputPath, options.Input.RootPath!);
            }
        }

        return ParseByFormat(options, rules, valueParsers, ctx);
    }

    private static IEnumerable<Dictionary<string, object?>> ParseByFormat(
        AnalysisOptions options,
        AnalysisRules? rules,
        IReadOnlyList<ValueParserConfig> valueParsers,
        AnalysisRunContext ctx)
    {
        string format = ResolveEffectiveFormat(options.Input.InputPath, rules, options.Input.Format);
        ctx.EffectiveFormat = format;

        if (rules?.ValueParsers is { Count: > 0 } && format == "excel-json")
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[规则驱动]][/] 因规则文件配置了 valueParsers，使用格式 '[blue]{format}[/]' 进行解析。");
        }
        else
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[自动检测]][/] 使用格式 '[blue]{format}[/]' 进行解析。");
        }

        AnalysisServices.Current.Console.MarkupLine(
            $"正在读取并解析文件: [blue]{MarkupText.Escape(options.Input.InputPath)}[/] ...");

        AnnounceCompression(options.Input.InputPath);

        var parseOptions = new ParseOptions
        {
            Flatten = ResolveFlatten(options),
            ValueParsers = valueParsers,
            CsvInferNumbers = options.Parse.CsvInferNumbers,
            SampleReservoir = options.Sampling.SampleReservoir,
            SampleStep = options.Sampling.SampleStep,
            StartFromLine = ctx.StartFromPosition
        };
        return FileParsers.ParseByFormat(options.Input.InputPath, format, parseOptions);
    }

    private static IEnumerable<Dictionary<string, object?>> ParseFromDatabase(
        AnalysisOptions options, IReadOnlyList<ValueParserConfig> valueParsers, AnalysisRunContext ctx)
    {
        ctx.EffectiveFormat = "db";
        var provider = DatabaseReader.ResolveProvider(options.Input.DbProvider, options.Input.ConnectionString!);
        AnalysisServices.Current.Console.MarkupLine(
            $"[grey][[数据库直连]][/] 使用 [blue]{DatabaseReader.DisplayName(provider)}[/] 数据源，流式读取查询结果...");
        AnalysisServices.Current.Console.MarkupLine(
            $"正在直连数据库并执行查询: [blue]{MarkupText.Escape(options.Input.Query!)}[/] ...");

        if (ctx.StartFromPosition > 0)
        {
            // P6.2 限制：数据库直连目前不支持行级 skip（DbDataReader 不暴露随机访问能力）。
            throw new NotSupportedException(
                "--resume 不支持数据库直连模式（--connection + --query）；DbDataReader 不暴露行级跳过能力。");
        }

        return DatabaseReader.ReadRows(options.Input.ConnectionString!, provider, options.Input.Query!);
    }

    /// <summary>
    /// 缺陷 2 修复落点：valueParsers 仅在输入确为电子表格类格式（excel / excel-json / csv，
    /// 单元格内嵌 JSON 的专用场景）时切换到 excel-json 解析器，不再强制改写其他格式——
    /// 普通 JSON / JSONL / XML / YAML 配置了 valueParsers 也按原格式解析（旧行为会对普通 JSON
    /// 报"字典键 '1' 缺失"）。宿主显式指定的格式（options.Input.Format，如 API 原始文本端点）优先于扩展名探测。
    /// </summary>
    public static string ResolveEffectiveFormat(string inputPath, AnalysisRules? rules, string? formatOverride)
    {
        string detected = !string.IsNullOrWhiteSpace(formatOverride)
            ? formatOverride.Trim().ToLowerInvariant()
            : FileParsers.DetectFormat(inputPath);
        if (rules?.ValueParsers is { Count: > 0 } && detected is "excel" or "excel-json" or "csv")
        {
            return "excel-json";
        }
        return detected;
    }

    private static void AnnounceCompression(string path)
    {
        var compression = CompressionDetector.Detect(path);
        if (compression == CompressionType.None)
            return;

        string label = compression switch
        {
            CompressionType.GZip => "gzip",
            CompressionType.BZip2 => "bzip2",
            CompressionType.Zip => "zip",
            _ => "压缩"
        };
        AnalysisServices.Current.Console.MarkupLine(
            $"[grey][[自动解压]][/] 检测到 {label} 压缩文件，正在流式解压后解析（保持行级流式，超大文件内存有界）...");

        // zip 含多个数据条目时提示确定性选择结果，避免"为什么统计的是这个文件"的困惑
        if (compression == CompressionType.Zip &&
            InputSource.PreviewZipEntries(path) is { } preview &&
            preview.Others.Count > 0)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[自动解压]][/] zip 含 {preview.Others.Count + 1} 个数据条目，已选择 [blue]{MarkupText.Escape(preview.Chosen)}[/]（其余条目忽略）");
        }
    }

    /// <summary>
    /// 流式地把 record-path 应用到每条记录上：每条记录可能 fan-out 为多条，因此这里只能把 source 物化为 list；
    /// 调用方传入的 source 在大多数解析路径上（jsonl/csv/excel/db/typed）已经是 list，因此实际成本可控。
    /// </summary>
    private static List<Dictionary<string, object?>> ApplyRecordPathStream(
        IEnumerable<Dictionary<string, object?>> source, string expression)
    {
        var materialized = source as IReadOnlyList<Dictionary<string, object?>> ?? source.ToList();
        var projected = new List<Dictionary<string, object?>>(materialized.Count);
        int noMatch = 0;
        foreach (var record in materialized)
        {
            var matches = JsonPathProjector.ApplyRecordPath(record, expression);
            if (matches.Count == 0)
            {
                noMatch++;
                continue;
            }
            projected.AddRange(matches);
        }

        if (noMatch > 0)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[yellow]警告: --record-path 表达式 {MarkupText.Escape(expression)} 在 {noMatch}/{materialized.Count} 条记录上无匹配；这些记录被跳过[/]");
        }
        if (projected.Count == 0 && materialized.Count > 0)
        {
            AnalysisServices.Current.Console.MarkupLine("[yellow]警告: --record-path 匹配到 0 个节点；将分析空集[/]");
        }
        else
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[green][[JSONPath]][/] --record-path 产出 [blue]{projected.Count}[/] 条记录（输入 {materialized.Count} 条）");
        }
        return projected;
    }

    private static IEnumerable<Dictionary<string, object?>> LoadAndApplyRootPath(string path, string expression)
    {
        // 整体读入 JSON：JSONPath 投影必须访问整个文档，无法流式。
        byte[] bytes;
        using (var fs = File.OpenRead(path))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes.AsSpan(3).ToArray();
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 根节点解析失败。", ex);
        }

        var records = JsonPathProjector.ApplyRootPath(root, expression);
        if (records.Count == 0)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[yellow]警告: --root-path 表达式 {MarkupText.Escape(expression)} 匹配到 0 个节点；将分析空集[/]");
        }
        else
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[green][[JSONPath]][/] --root-path 提取了 [blue]{records.Count}[/] 条记录");
        }
        return records;
    }

    // ── 采样 / 预览 / 统计回填 ──

    /// <summary>
    /// 采样包装：仅对解析器内未按行采样的格式生效（jsonl/csv/excel/excel-json 已在解析器内采样）。
    /// 顺序在字段过滤之后——从过滤后的总体中抽样，保证样本量语义正确。
    /// </summary>
    private static IEnumerable<Dictionary<string, object?>> ApplySamplingIfEnabled(
        IEnumerable<Dictionary<string, object?>> parsed, AnalysisOptions options, AnalysisRunContext ctx)
    {
        if (!Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            return parsed;

        bool parserAlreadySampled = ctx.EffectiveFormat is "jsonl" or "ndjson" or "csv" or "excel" or "excel-json";

        AnnounceSampling(options);
        return parserAlreadySampled ? parsed : Sampling.Apply(parsed, options.Sampling.SampleReservoir, options.Sampling.SampleStep);
    }

    private static void AnnounceSampling(AnalysisOptions options)
    {
        if (options.Sampling.SampleReservoir > 0)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[采样]][/] 蓄水池随机抽样：从海量数据中均匀抽取最多 [blue]{options.Sampling.SampleReservoir}[/] 条进行统计与 Schema 概览。");
        }
        else
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[采样]][/] 等距系统抽样：每 [blue]{options.Sampling.SampleStep}[/] 条记录取 1 条进行统计与 Schema 概览。");
        }
    }

    /// <summary>预览包装：惰性捕获流经的前 limit 条记录（与统计共用同一次枚举，不额外物化）。</summary>
    private static IEnumerable<Dictionary<string, object?>> WrapWithPreview(
        IEnumerable<Dictionary<string, object?>> source, List<Dictionary<string, object?>> preview, int limit)
    {
        foreach (var obj in source)
        {
            if (preview.Count < limit)
            {
                lock (preview)
                {
                    if (preview.Count < limit) preview.Add(obj);
                }
            }
            yield return obj;
        }
    }

    /// <summary>
    /// 进度桥接：把分析批计数扇出到宿主进度 UI（uiProgress）、宿主桥（ProgressBridge），
    /// 并始终维护 <see cref="AnalysisRunContext.LastRecordCount"/>（--checkpoint 落盘依赖）。
    /// </summary>
    private static Progress<long> BuildProgressBridge(IProgress<long>? uiProgress, AnalysisRunContext ctx)
    {
        return new Progress<long>(count =>
        {
            uiProgress?.Report(count);
            ctx.ProgressBridge?.Report(count);
            ctx.LastRecordCount = count;
            if (ctx.MemoryBudget is { IsEnabled: true } mem)
            {
                mem.Check(count, out _);
            }
        });
    }

    private static void BuildSamplingConfidence(AnalysisResult result, AnalysisOptions options)
    {
        if (!Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            return;

        long effective = options.Sampling.SampleReservoir > 0
            ? options.Sampling.SampleReservoir
            : Math.Max(1, result.TotalObjects / Math.Max(1, options.Sampling.SampleStep));
        long estimated = result.TotalObjects;
        var fieldCounts = result.FieldStatistics.Select(s => (s.FieldName, s.Count));
        result.SamplingConfidence = SamplingConfidenceCalculator.Build(
            method: options.Sampling.SampleReservoir > 0 ? "reservoir" : "systematic",
            sampleSize: effective,
            estimatedTotal: estimated,
            fieldCounts: fieldCounts,
            total: estimated);
    }

    private static List<string>? SplitFields(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return null;
        return FieldPath.ParseList(fields).Select(path => path.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<ValueParserConfig> ResolveValueParsers(AnalysisRules? rules)
    {
        if (rules?.ValueParsers is not { Count: > 0 })
            return Array.Empty<ValueParserConfig>();

        return rules.ValueParsers
            .Where(p => !string.IsNullOrWhiteSpace(p.Source))
            .Select(p => new ValueParserConfig
            {
                Source = p.Source.Trim(),
                ParseType = string.IsNullOrWhiteSpace(p.ParseType) ? "auto" : p.ParseType.Trim(),
                Flatten = p.Flatten,
                Header = p.Header
            })
            .ToList();
    }

    /// <summary>
    /// 展平判定：--record-path 必须看到嵌套子对象才能正确投影；启用 --record-path 时强制不展平，
    /// 投影提取出的子对象本身就是平坦的。
    /// </summary>
    public static bool ResolveFlatten(AnalysisOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Input.RecordPath))
            return false;

        return options.Parse.Flatten;
    }

    private static long? EstimateTotalObjects(List<Dictionary<string, object?>> materialized)
    {
        return materialized.Count > 0 ? materialized.Count : null;
    }
}
