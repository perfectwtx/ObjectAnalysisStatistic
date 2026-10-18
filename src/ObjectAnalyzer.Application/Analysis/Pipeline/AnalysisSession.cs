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
internal sealed class AnalysisSession
{
    private readonly IAnalysisInputServices _inputs;
    private readonly IProgressReporter _progress;
    private readonly AnalysisRunContext _context;
    private readonly CancellationToken _cancellation;
    private readonly bool _asyncInput;
    private MemoryBudget? _memory;
    private long _populationCount;
    private AnalysisResourceLimits? _limits;
    private bool _previewTruncated;

    public AnalysisSession(IAnalysisInputServices inputs, IProgressReporter progress, AnalysisRunContext context, CancellationToken cancellation, bool asyncInput = false)
    { _inputs = inputs; _progress = progress; _context = context; _cancellation = cancellation; _asyncInput = asyncInput; }

    public PipelineResult Run(
        AnalysisOptions options,
        AnalysisRules? rules)
    {
        var ctx = _context;
        var cancellationToken = _cancellation;
        Stage(AnalysisStage.Preparing);
        _limits = options.Memory.Resources;
        _memory = ctx.MemoryBudget ?? new MemoryBudget(options.Memory.MaxMemoryMB, options.Memory.MaxMemoryMode);
        _memory.Check(0, out _);
        _memory.ThrowIfHardExceeded();
        if (_memory.IsEnabled && _memory.Mode == MaxMemoryMode.Soft && !Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
        {
            if (options.Sampling.MemoryFallbackSampleSize < 2) throw new ArgumentException("Soft memory mode requires a fallback reservoir size of at least two.");
            options.Sampling.SampleReservoir = options.Sampling.MemoryFallbackSampleSize;
            Sampling.Validate(options.Sampling.SampleReservoir, options.Sampling.SampleStep);
            Diagnostic("memory.sampling", "Soft memory mode selects reservoir sampling before input is read.");
        }
        if ((ctx.StartFromPosition > 0 || ctx.InitialState is not null) &&
            Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            throw new NotSupportedException("Incremental state cannot be combined with sampling.");
        var accumulator = new AnalysisAccumulator(rules, options.Advanced, options.Parse.MaxTrackedValues, ctx.Analyzers, options.Memory.Resources);

        // Prepare filters and transformations before opening the input.
        if (!string.IsNullOrWhiteSpace(options.Input.RootPath)) _inputs.ValidateProjection(options.Input.RootPath);
        if (!string.IsNullOrWhiteSpace(options.Input.RecordPath)) _inputs.ValidateProjection(options.Input.RecordPath);
        var transformProcessor = _inputs.CreateTransformer(rules, SplitFields(options.FieldSelection.Fields), options.FieldSelection.Filter);
        if (ctx.InitialState is { } initial)
        {
            initial.EnsureMergeable();
            accumulator.EnsureMergeable();
            if (initial.Configuration != accumulator.Configuration)
                throw new InvalidOperationException("Initial state configuration differs from the execution plan.");
        }
        var input = PrepareInput(options, rules);
        Stage(AnalysisStage.Input);
        // 1. 解析分发：数据库直连 / root-path 整体投影 / 常规文件
        IEnumerable<Dictionary<string, object?>> objects = WithCancellation(ReadInput(input.Source, input.Options));
        Stage(AnalysisStage.Projection);

        // 2. record-path 投影：每条记录可能 fan-out，始终生效（即使在 root-path 之后）
        if (!string.IsNullOrWhiteSpace(options.Input.RecordPath))
        {
            objects = ApplyRecordPathStream(objects, options.Input.RecordPath!);
        }

        // 3. 字段选择 + 值转换 + 过滤（--fields / runtime.selectedFields / transform / --where / runtime.filter）
        Stage(AnalysisStage.Transformation);
        Stage(AnalysisStage.Filtering);
        if (transformProcessor.HasSelectionOrTransforms)
        {
            objects = transformProcessor.Wrap(objects.Select(ToObjectRecord));
        }

        // 4. 全部格式共用转换、过滤后的采样阶段。
        Stage(AnalysisStage.Sampling);
        objects = ApplySamplingIfEnabled(objects, options);

        // 5. 数据预览捕获（Web）：在进入统计前捕获前 N 条（转换/采样后）
        List<Dictionary<string, object?>>? preview = null;
        if (ctx.PreviewSink != null)
        {
            preview = new();
            objects = WrapWithPreview(objects, preview, Math.Min(ctx.PreviewLimit, options.Memory.Resources.MaxPreviewRecords), options.Memory.Resources);
        }

        // 6. 关系跟踪：与统计共用同一次枚举，记录嵌套对象/对象集合关系，避免再次读取输入。
        IReadOnlyList<ObjectRelationship> objectRelationships = Array.Empty<ObjectRelationship>();
        objects = ObjectRelationshipAnalyzer.Track(objects, relationships => objectRelationships = relationships, options.Memory.Resources);

        // Advanced collectors observe the same stream; distribution sketches are produced by the accumulator.
        FuzzyDuplicateCollector? fuzzy = !string.IsNullOrWhiteSpace(options.Advanced.FuzzyDedupField)
            ? new(options.Memory.Resources) : null;
        if (fuzzy is not null) objects = ObserveFuzzy(objects, fuzzy, FieldPath.Parse(options.Advanced.FuzzyDedupField!).ToString());

        // 9. 统计：进度端口包装（CLI：Spectre 进度条；API/Web：直接执行）
        AnalysisResult result = null!;
        Dictionary<string, MergeableQuantileSketch>? quantileSketches = null;
        Dictionary<string, HyperLogLog>? distinctSketches = null;

        long? expectedTotal = null;
        string description = string.IsNullOrEmpty(options.Input.InputPath)
            ? "分析中（数据库直连）"
            : $"分析中 {Path.GetFileName(options.Input.InputPath)}";

        Stage(AnalysisStage.Observation);
        _progress.Run(description, expectedTotal, options.Progress.ShowProgress, uiProgress =>
        {
            IProgress<long> progress = BuildProgressBridge(uiProgress, ctx);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Parse.MaxDegreeOfParallelism > 0
                    ? options.Parse.MaxDegreeOfParallelism
                    : -1
            };
            accumulator.ObserveRange(objects,
                parallelOptions, progress, cancellationToken);
            ctx.LastRecordCount = accumulator.TotalObjects;
            progress.Report(ctx.LastRecordCount);
            cancellationToken.ThrowIfCancellationRequested();
            Stage(AnalysisStage.Completion);
            var detailed = accumulator.CompleteDetailed();
            result = detailed.Result;
            quantileSketches = detailed.QuantileSketches;
            distinctSketches = detailed.DistinctSketches;
        });

        // 10. 回填与补充统计
        if (fuzzy is not null)
        {
            result.FuzzyDuplicates = fuzzy.Complete(cancellationToken);
            result.FuzzyDuplicates.Field = FieldPath.Parse(options.Advanced.FuzzyDedupField!).ToString();
            result.Reliability!.Limitations.Add(new("fuzzy-duplicates", AnalysisAccuracy.Approximate,
                "Canonical counts cover the stream; Jaccard searches singleton keys only (at most 5000); reported groups and examples are bounded."));
        }
        result.ObjectRelationships = objectRelationships.ToList();
        transformProcessor.ApplyFailures(result);
        result.FilterExpression = options.FieldSelection.Filter;
        accumulator.CapturePipelineResult(result);
        if (ctx.InitialState is not null)
        {
            accumulator.Merge(ctx.InitialState);
            var completed = accumulator.CompleteDetailed();
            result = completed.Result;
            quantileSketches = completed.QuantileSketches;
            distinctSketches = completed.DistinctSketches;
        }
        BuildSamplingConfidence(result, options, _populationCount);
        if (_previewTruncated)
            result.Reliability!.Limitations.Add(new("preview", AnalysisAccuracy.Truncated, "Preview records and retained bytes have independent limits; statistics cover every selected record."));
        CheckMemory();

        // 11. 预览回调（统计成功完成后一次性交付）
        cancellationToken.ThrowIfCancellationRequested();
        if (ctx.PreviewSink is { } sink) ctx.Notify(() => sink(preview ?? new List<Dictionary<string, object?>>()));

        return new PipelineResult
        {
            Result = result,
            ExecutionProfile = ctx.ExecutionProfile,
            RetainedPayloadBytes = accumulator.Payload.Bytes,
            Accumulator = Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep)
                || options.Memory.MaxMemoryMB > 0 && options.Memory.MaxMemoryMode == MaxMemoryMode.Soft ? null : accumulator,
            ExecutionSignature = CreateExecutionSignature(options),
            QuantileSketches = quantileSketches,
            DistinctSketches = distinctSketches
        };
    }

    // ── 解析分发 ──

    internal static string CreateExecutionSignature(AnalysisOptions options) => RecordValue.FromJsonElement(
        JsonSerializer.SerializeToElement(new
        {
            InputContract = "record-source/v1",
            options.Parse.Flatten, options.Parse.MaxTrackedValues, options.Parse.CsvInferNumbers, options.Parse.MalformedRecords, options.FieldSelection,
            options.Input.RootPath, options.Input.RecordPath, options.Sampling, options.Advanced, options.Memory.Resources
        })).CanonicalKey;

    private (IAnalysisDataSource Source, SourceReadOptions Options) PrepareInput(AnalysisOptions options, AnalysisRules? rules)
    {
        IAnalysisDataSource source = _context.Source ?? _inputs.Open(options, rules, _context.Parsers);
        string? rootPath = options.Input.RootPath;
        if (!string.IsNullOrWhiteSpace(rootPath) && !source.Descriptor.Capabilities.RootProjection)
        {
            Diagnostic("projection.root-ignored", "Root projection is ignored for record-oriented input.");
            rootPath = null;
        }
        var settings = new SourceReadOptions
        {
            ResourceLimits = options.Memory.Resources, CheckMemory = CheckMemory,
            Flatten = ResolveFlatten(options), CsvInferNumbers = options.Parse.CsvInferNumbers,
            ValueParsers = ResolveValueParsers(rules), StartPosition = _context.StartFromPosition, RootPath = rootPath,
            MalformedRecords = options.Parse.MalformedRecords, DiagnosticLimit = Math.Min(options.Parse.InputDiagnosticLimit, options.Memory.Resources.MaxDiagnosticSamples),
            SkippedRecordProgress = count => _context.SkippedInputRecords = count,
            PositionProgress = position =>
            {
                _cancellation.ThrowIfCancellationRequested();
                _context.LastSourcePosition = position - _context.StartFromPosition;
            },
            DiagnosticSink = diagnostic =>
            {
                if (diagnostic.Code == "input.skipped-summary") _context.SkippedInputRecords = diagnostic.Occurrences;
                Diagnostic(diagnostic.Code, diagnostic.Message, new Dictionary<string, object?>
                { ["location"] = diagnostic.Location, ["occurrences"] = diagnostic.Occurrences });
            }
        };
        settings.Validate(source.Descriptor);
        _context.SourceDescriptor = source.Descriptor;
        _context.ExecutionProfile = DescribeExecution(source.Descriptor, options);
        return (source, settings);
    }

    private IEnumerable<Dictionary<string, object?>> ReadInput(IAnalysisDataSource source, SourceReadOptions settings)
    {
        _context.EffectiveFormat = source.Descriptor.Format;
        Diagnostic("input.format", "Input format resolved.", new Dictionary<string, object?> { ["format"] = source.Descriptor.Format });
        if (_context.Source is null && !string.IsNullOrWhiteSpace(_context.SourceDescriptor?.Name)
            && source.Descriptor.Format != "db") AnnounceCompression(source.Descriptor.Name);
        var input = _asyncInput ? new AsyncRecordBridge(source) : source;
        return input.Read(settings, _cancellation).Select(record => record.ToEngineRecord());
    }

    private static Dictionary<string, object?> ToObjectRecord(Dictionary<string, object?> record)
        => record.ToDictionary(pair => pair.Key, pair => pair.Value is RecordValue typed ? typed.ToObject() : pair.Value, StringComparer.Ordinal);

    private void AnnounceCompression(string path)
    {
        if (_inputs.DescribeCompression(path) is { } data)
            Diagnostic("input.compression", "Compressed input detected.", data);
    }

    /// <summary>Lazy, cancellable record projection; no full-input materialization.</summary>
    private IEnumerable<Dictionary<string, object?>> ApplyRecordPathStream(
        IEnumerable<Dictionary<string, object?>> source, string expression)
    {
        long input = 0, missing = 0, output = 0;
        foreach (var record in source)
        {
            _cancellation.ThrowIfCancellationRequested();
            input++;
            var matches = _inputs.ProjectRecord(ToObjectRecord(record), expression);
            if (matches.Count == 0) missing++;
            foreach (var match in matches) { output++; yield return match; }
        }
        Diagnostic("projection.summary", "Record projection completed.", new Dictionary<string, object?>
        { ["input"] = input, ["unmatched"] = missing, ["output"] = output });
    }

    // ── 采样 / 预览 / 统计回填 ──

    /// <summary>
    /// 采样包装：所有解析器关闭内部采样，在这里统一应用。
    /// 顺序在字段过滤之后——从过滤后的总体中抽样，保证样本量语义正确。
    /// </summary>
    private IEnumerable<Dictionary<string, object?>> ApplySamplingIfEnabled(
        IEnumerable<Dictionary<string, object?>> parsed, AnalysisOptions options)
    {
        if (!Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            return parsed;

        AnnounceSampling(options);
        return Sampling.Apply(CountPopulation(parsed), options.Sampling.SampleReservoir, options.Sampling.SampleStep, options.Memory.Resources);
    }

    private IEnumerable<Dictionary<string, object?>> CountPopulation(IEnumerable<Dictionary<string, object?>> source)
    {
        foreach (var record in source) { _populationCount++; yield return record; }
    }

    private void AnnounceSampling(AnalysisOptions options)
        => Diagnostic("sampling.enabled", "Sampling is applied after transformations and filtering.",
            new Dictionary<string, object?> { ["reservoir"] = options.Sampling.SampleReservoir, ["step"] = options.Sampling.SampleStep });

    /// <summary>预览包装：惰性捕获流经的前 limit 条记录（与统计共用同一次枚举，不额外物化）。</summary>
    private IEnumerable<Dictionary<string, object?>> WrapWithPreview(
        IEnumerable<Dictionary<string, object?>> source, List<Dictionary<string, object?>> preview, int limit, AnalysisResourceLimits limits)
    {
        long bytes = 0;
        foreach (var obj in source)
        {
            long size = ObjectAnalyzer.EstimateRecordBytes(obj, limits);
            if (preview.Count < limit && bytes + size <= limits.MaxPreviewBytes)
            {
                lock (preview)
                {
                    if (preview.Count < limit) { preview.Add(ToObjectRecord(obj)); bytes += size; }
                }
            }
            else _previewTruncated = true;
            yield return obj;
        }
    }

    /// <summary>
    /// 进度桥接：把分析批计数扇出到宿主进度 UI（uiProgress）、宿主桥（ProgressBridge），
    /// 并始终维护 <see cref="AnalysisRunContext.LastRecordCount"/>（--checkpoint 落盘依赖）。
    /// </summary>
    private InlineProgress BuildProgressBridge(IProgress<long>? uiProgress, AnalysisRunContext ctx)
    {
        return new InlineProgress(count =>
        {
            _cancellation.ThrowIfCancellationRequested();
            ctx.LastRecordCount = count;
            if (_memory is { IsEnabled: true } mem)
            { mem.Check(count, out _); mem.ThrowIfHardExceeded(); }
            ctx.Notify(() => uiProgress?.Report(count));
            ctx.Notify(() => ctx.ProgressBridge?.Report(count));
            ctx.Publish(new AnalysisEvent(ctx.RunId, AnalysisEventKind.Progress, AnalysisStage.Observation, count));
        });
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    { public void Report(long value) => report(value); }

    private void Stage(AnalysisStage stage)
    {
        _cancellation.ThrowIfCancellationRequested();
        _context.CurrentStage = stage;
        _context.Publish(new AnalysisEvent(_context.RunId, AnalysisEventKind.StageStarted, stage));
    }

    private void Diagnostic(string code, string message, IReadOnlyDictionary<string, object?>? data = null)
        => _context.Publish(new AnalysisEvent(_context.RunId, AnalysisEventKind.Diagnostic, _context.CurrentStage,
            Diagnostic: new AnalysisDiagnostic(code, message, data)));

    private IEnumerable<Dictionary<string, object?>> WithCancellation(IEnumerable<Dictionary<string, object?>> source)
    {
        using var iterator = source.GetEnumerator();
        while (true)
        {
            _cancellation.ThrowIfCancellationRequested();
            CheckMemory();
            if (!iterator.MoveNext()) break;
            CheckMemory();
            _cancellation.ThrowIfCancellationRequested();
            ObjectAnalyzer.EstimateRecordBytes(iterator.Current, _limits!);
            yield return iterator.Current;
        }
    }

    private static void BuildSamplingConfidence(AnalysisResult result, AnalysisOptions options, long populationCount)
    {
        if (!Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            return;

        result.Reliability ??= new();
        result.Reliability.Limitations.Add(new("analysis-population", AnalysisAccuracy.Sampled, "Statistics describe records selected after transformation and filtering."));
        long effective = result.TotalObjects;
        long estimated = populationCount;
        var fieldCounts = result.FieldStatistics.Select(s => (s.FieldName, s.Count));
        result.SamplingConfidence = SamplingConfidenceCalculator.Build(
            method: options.Sampling.SampleReservoir > 0 ? "reservoir" : "systematic",
            sampleSize: effective,
            estimatedTotal: estimated,
            fieldCounts: fieldCounts,
            total: result.TotalObjects);
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

    private void CheckMemory()
    {
        if (_memory is not { IsEnabled: true }) return;
        _memory.Check(_context.LastRecordCount, out _); _memory.ThrowIfHardExceeded();
    }

    private static IEnumerable<Dictionary<string, object?>> ObserveFuzzy(
        IEnumerable<Dictionary<string, object?>> source, FuzzyDuplicateCollector collector, string field)
    {
        foreach (var record in source)
        { if (record.TryGetValue(field, out var value)) collector.Observe(value?.ToString()); yield return record; }
    }

    internal static AnalysisExecutionProfile DescribeExecution(SourceDescriptor source, AnalysisOptions options)
    {
        var limits = options.Memory.Resources;
        var capabilities = new List<AnalysisCapabilityRequirement>
        {
            new("input", source.Capabilities.Streaming ? AnalysisAccessMode.SinglePass : AnalysisAccessMode.BufferedInput,
                source.Capabilities.Streaming ? null : limits.MaxBufferedInputBytes),
            new("statistics", AnalysisAccessMode.BoundedState, limits.MaxRetainedPayloadBytes),
            new("exact-object-identities", AnalysisAccessMode.BoundedState, limits.MaxObjectIdentities),
            new("preview", AnalysisAccessMode.BoundedState, limits.MaxPreviewBytes)
        };
        if (source.Capabilities.RequiresSeek) capabilities.Add(new("parser", AnalysisAccessMode.SeekableInput, limits.MaxBufferedInputBytes));
        if (options.Sampling.SampleReservoir > 0) capabilities.Add(new("reservoir", AnalysisAccessMode.BoundedSample, limits.MaxSamplingBytes));
        if (options.Advanced.EnableCorrelation) capabilities.Add(new("correlation", AnalysisAccessMode.BoundedSample, Math.Min(options.Advanced.CorrelationReservoir, limits.MaxCorrelationRows)));
        if (!string.IsNullOrWhiteSpace(options.Advanced.FuzzyDedupField)) capabilities.Add(new("fuzzy-duplicates", AnalysisAccessMode.BoundedState, limits.MaxFuzzyKeys));
        return new(capabilities, limits.BatchSize, Math.Min(options.Parse.MaxDegreeOfParallelism > 0 ? options.Parse.MaxDegreeOfParallelism : limits.MaxWorkers, limits.MaxWorkers));
    }
}
