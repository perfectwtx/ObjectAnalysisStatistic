using System.Text.Json;

namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>An immutable configuration snapshot. Mutable computation state belongs to each execution.</summary>
public sealed class AnalysisExecutionPlan
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 512 };
    private readonly string _options;
    private readonly string? _rules;

    public AnalysisExecutionPlan(AnalysisOptions options, AnalysisRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = JsonSerializer.Serialize(options, JsonOptions);
        _rules = rules is null ? null : JsonSerializer.Serialize(rules, JsonOptions);
        var copy = CreateOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(copy.Parse.MaxTrackedValues);
        ArgumentOutOfRangeException.ThrowIfNegative(copy.Sampling.SampleReservoir);
        ArgumentOutOfRangeException.ThrowIfNegative(copy.Sampling.SampleStep);
        if (copy.Parse.MaxDegreeOfParallelism is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(options), "Parallelism must be -1 or positive.");
        if (copy.Sampling.SampleReservoir > 0 && copy.Sampling.SampleStep > 0)
            throw new ArgumentException("Reservoir and systematic sampling are mutually exclusive.", nameof(options));
        Sampling.Validate(copy.Sampling.SampleReservoir, copy.Sampling.SampleStep);
        if (!string.IsNullOrWhiteSpace(copy.Input.RootPath)) JsonPathProjector.TryValidate(copy.Input.RootPath);
        if (!string.IsNullOrWhiteSpace(copy.Input.RecordPath)) JsonPathProjector.TryValidate(copy.Input.RecordPath);
        ArgumentOutOfRangeException.ThrowIfNegative(copy.Memory.MaxMemoryMB);
        if (!string.IsNullOrWhiteSpace(copy.Input.ConnectionString) && string.IsNullOrWhiteSpace(copy.Input.Query))
            throw new ArgumentException("A database query is required.", nameof(options));
    }

    internal AnalysisOptions CreateOptions() => JsonSerializer.Deserialize<AnalysisOptions>(_options, JsonOptions)!;
    internal AnalysisRules? CreateRules() => _rules is null ? null : JsonSerializer.Deserialize<AnalysisRules>(_rules, JsonOptions);
}

/// <summary>Reusable execution entry. Dependencies are fixed per instance; all mutable state is per run.</summary>
public sealed class AnalysisExecutor(IProgressReporter? progress = null)
{
    private readonly IProgressReporter _progress = progress ?? DirectProgressReporter.Instance;

    public PipelineResult Execute(AnalysisOptions options, AnalysisRules? rules = null,
        AnalysisRunContext? runContext = null, CancellationToken cancellationToken = default)
        => ExecuteRun(() => new AnalysisExecutionPlan(options, rules), runContext, cancellationToken);

    public PipelineResult Execute(AnalysisExecutionPlan plan, AnalysisRunContext? runContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecuteRun(() => plan, runContext, cancellationToken);
    }

    public Task<PipelineResult> ExecuteAsync(AnalysisOptions options, AnalysisRules? rules = null,
        AnalysisRunContext? runContext = null, CancellationToken cancellationToken = default)
    {
        // Freeze before scheduling so callers can safely reuse their mutable option builders.
        AnalysisExecutionPlan? plan = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        try { plan = new AnalysisExecutionPlan(options, rules); }
        catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        return Task.Run(() => ExecuteRun(() =>
        {
            failure?.Throw();
            return plan!;
        }, runContext, cancellationToken, asyncInput: true), CancellationToken.None);
    }

    public Task<PipelineResult> ExecuteAsync(AnalysisExecutionPlan plan, AnalysisRunContext? runContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ExecuteRun(() => plan, runContext, cancellationToken, asyncInput: true), CancellationToken.None);
    }

    private PipelineResult ExecuteRun(Func<AnalysisExecutionPlan> prepare, AnalysisRunContext? runContext,
        CancellationToken cancellationToken, bool asyncInput = false)
    {
        var context = runContext ?? new AnalysisRunContext();
        context.Begin();
        PipelineResult result;
        try
        {
            AnalysisExecutionPlan plan;
            try { plan = prepare(); }
            finally
            {
                context.Publish(new AnalysisEvent(context.RunId, AnalysisEventKind.Started));
                context.Notify(() => context.ExecutionObserver?.OnStarted());
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (context.StartFromPosition < 0 || context.PreviewLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(runContext), "Cursor and preview limit cannot be negative.");
            result = new AnalysisSession(_progress, context, cancellationToken, asyncInput)
                .Run(plan.CreateOptions(), plan.CreateRules());
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.Publish(new AnalysisEvent(context.RunId, AnalysisEventKind.Cancelled, context.CurrentStage));
            context.Notify(() => context.ExecutionObserver?.OnCancelled());
            throw;
        }
        catch (Exception exception)
        {
            context.Publish(new AnalysisEvent(context.RunId, AnalysisEventKind.Failed, context.CurrentStage,
                Failure: AnalysisFailure.Classify(exception, context.CurrentStage), Exception: exception));
            context.Notify(() => context.ExecutionObserver?.OnFailed(exception));
            throw;
        }

        context.Publish(new AnalysisEvent(context.RunId, AnalysisEventKind.Completed, AnalysisStage.Completion, context.LastRecordCount));
        context.Notify(() => context.ExecutionObserver?.OnCompleted(result));
        return result;
    }
}
