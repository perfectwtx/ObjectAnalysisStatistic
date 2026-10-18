using System.Collections.Concurrent;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Data;
using System.Text.Json;

namespace ObjectAnalyzer.Tests;

public sealed class AnalysisExecutorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oa-executor-" + Guid.NewGuid().ToString("N"));

    public AnalysisExecutorTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AnalysisOptions Input(string name, string content)
    {
        string path = Path.Combine(_directory, name + ".jsonl");
        File.WriteAllText(path, content);
        return new AnalysisOptions { Input = { InputPath = path }, Parse = { MaxDegreeOfParallelism = 1 } };
    }

    private static bool IsTerminal(AnalysisEvent item)
        => item.Kind is AnalysisEventKind.Completed or AnalysisEventKind.Cancelled or AnalysisEventKind.Failed;

    [Fact]
    public async Task ConcurrentRuns_IsolateConfigurationProgressAndEvents()
    {
        var executor = new AnalysisExecutor();
        var first = Input("first", "{\"x\":1}\n{\"x\":2}\n");
        var second = Input("second", "{\"y\":3}\n");
        first.Parse.MaxTrackedValues = 0;
        var eventsA = new List<AnalysisEvent>();
        var eventsB = new List<AnalysisEvent>();
        var progressA = new List<long>();
        var progressB = new List<long>();
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        AnalysisRunContext Context(List<AnalysisEvent> events, List<long> counts) => new()
        {
            EventSink = item =>
            {
                events.Add(item);
                if (item.Kind == AnalysisEventKind.Started)
                {
                    entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                }
            },
            ProgressBridge = new InlineProgress(counts.Add)
        };
        var contextA = Context(eventsA, progressA);
        var contextB = Context(eventsB, progressB);
        var taskA = Task.Run(() => executor.Execute(first, runContext: contextA));
        var taskB = Task.Run(() => executor.Execute(second, runContext: contextB));
        bool overlapping = entered.Wait(TimeSpan.FromSeconds(15));
        release.Set();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.True(overlapping);
        Assert.Equal(2, results[0].Result.TotalObjects);
        Assert.Empty(Assert.Single(results[0].Result.FieldStatistics).ValueCounts);
        Assert.Equal("y", Assert.Single(results[1].Result.FieldStatistics).FieldName);
        Assert.All(eventsA, item => Assert.Equal(contextA.RunId, item.RunId));
        Assert.All(eventsB, item => Assert.Equal(contextB.RunId, item.RunId));
        Assert.Equal(AnalysisEventKind.Completed, Assert.Single(eventsA, IsTerminal).Kind);
        Assert.Equal(AnalysisEventKind.Completed, Assert.Single(eventsB, IsTerminal).Kind);
        Assert.Equal(2, progressA[^1]);
        Assert.Equal(1, progressB[^1]);
        Assert.Empty(contextA.ObserverErrors);
        Assert.Empty(contextB.ObserverErrors);
        Assert.Equal(Enum.GetValues<AnalysisStage>(), eventsA.Where(item => item.Kind == AnalysisEventKind.StageStarted).Select(item => item.Stage!.Value));
    }

    [Fact]
    public void ExecutionPlan_FreezesOptionsRulesAndNestedCollections()
    {
        var options = Input("frozen", "{\"x\":1}\n{\"x\":2}\n");
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { MinValue = 2 } } };
        var plan = new AnalysisExecutionPlan(options, rules);
        options.Input.InputPath = "missing";
        options.FieldSelection.Fields = "other";
        rules.Fields["x"].MinValue = 0;
        var executor = new AnalysisExecutor();
        var first = executor.Execute(plan).Result;
        var second = executor.Execute(plan).Result;
        Assert.Equal(2, first.TotalObjects);
        Assert.Contains(first.QualityViolations, item => item.Check == "range" && item.Count == 1);
        Assert.Equal(first.TotalObjects, second.TotalObjects);
    }

    [Fact]
    public void StartedObserver_CannotMutateFrozenRunConfiguration()
    {
        var options = Input("callback", "{\"x\":1}\n");
        var result = new AnalysisExecutor().Execute(options, runContext: new AnalysisRunContext
        {
            EventSink = item => { if (item.Kind == AnalysisEventKind.Started) options.Input.InputPath = "missing"; }
        });
        Assert.Equal(1, result.Result.TotalObjects);
    }

    [Fact]
    public void ObserverExceptions_DoNotChangeSuccessfulOutcomeOrDuplicateTerminalEvent()
    {
        var options = Input("observer", "{\"x\":1}\n");
        var events = new List<AnalysisEvent>();
        var observer = new ThrowingObserver();
        var context = new AnalysisRunContext
        {
            ExecutionObserver = observer,
            EventSink = item => { events.Add(item); throw new InvalidOperationException("event listener"); },
            ProgressBridge = new InlineProgress(_ => throw new InvalidOperationException("progress listener")),
            PreviewSink = _ => throw new InvalidOperationException("preview listener")
        };
        Assert.Equal(1, new AnalysisExecutor().Execute(options, runContext: context).Result.TotalObjects);
        Assert.Equal(AnalysisEventKind.Completed, Assert.Single(events, IsTerminal).Kind);
        Assert.Equal(1, observer.Started);
        Assert.Equal(1, observer.Completed);
        Assert.Equal(0, observer.Failed);
        Assert.NotEmpty(context.ObserverErrors);
    }

    [Fact]
    public void CancellationDuringReading_ReleasesFileAndEmitsOneTerminalEvent()
    {
        var options = Input("cancel", string.Concat(Enumerable.Repeat("{\"x\":1}\n", 25000)));
        using var cancellation = new CancellationTokenSource();
        var events = new List<AnalysisEvent>();
        var context = new AnalysisRunContext
        {
            EventSink = events.Add,
            ProgressBridge = new InlineProgress(_ => cancellation.Cancel())
        };
        Assert.ThrowsAny<OperationCanceledException>(() => new AnalysisExecutor().Execute(options,
            runContext: context, cancellationToken: cancellation.Token));
        Assert.Equal(AnalysisEventKind.Cancelled, Assert.Single(events, IsTerminal).Kind);
        Assert.InRange(context.LastRecordCount, 1, 24999);
        using var exclusive = new FileStream(options.Input.InputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void PreCancelledRun_DoesNotOpenInput()
    {
        var options = new AnalysisOptions { Input = { InputPath = Path.Combine(_directory, "missing.jsonl") } };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var events = new List<AnalysisEvent>();
        Assert.ThrowsAny<OperationCanceledException>(() => new AnalysisExecutor().Execute(options,
            runContext: new AnalysisRunContext { EventSink = events.Add }, cancellationToken: cancellation.Token));
        Assert.Equal(AnalysisEventKind.Started, events[0].Kind);
        Assert.Equal(AnalysisEventKind.Cancelled, Assert.Single(events, IsTerminal).Kind);
    }

    [Fact]
    public void InvalidRule_FailsBeforeInputAndClassifiesFailure()
    {
        var options = new AnalysisOptions { Input = { InputPath = "missing.jsonl" } };
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "[" } } };
        var events = new List<AnalysisEvent>();
        Assert.ThrowsAny<ArgumentException>(() => new AnalysisExecutor().Execute(options, rules,
            new AnalysisRunContext { EventSink = events.Add }));
        var failed = Assert.Single(events, IsTerminal);
        Assert.Equal(AnalysisEventKind.Failed, failed.Kind);
        Assert.Equal(AnalysisFailureKind.Configuration, failed.Failure);
        Assert.DoesNotContain(events, item => item.Stage == AnalysisStage.Input);
    }

    [Fact]
    public void UnrelatedCancellationException_IsFailure()
    {
        var options = Input("unrelated", "{\"x\":1}\n");
        var events = new List<AnalysisEvent>();
        Assert.Throws<OperationCanceledException>(() => new AnalysisExecutor(new BrokenReporter()).Execute(options,
            runContext: new AnalysisRunContext { EventSink = events.Add }));
        Assert.Equal(AnalysisEventKind.Failed, Assert.Single(events, IsTerminal).Kind);
    }

    [Fact]
    public void Sampling_AppliesAfterFilteringForAllFormats()
    {
        const string lines = "{\"x\":0}\n{\"x\":1}\n{\"x\":2}\n{\"x\":3}\n";
        var options = Input("sample", lines);
        options.FieldSelection.Filter = "x >= 1";
        options.Sampling.SampleStep = 2;
        var jsonl = new AnalysisExecutor().Execute(options).Result;
        string json = Path.Combine(_directory, "sample.json");
        File.WriteAllText(json, "[" + string.Join(',', lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]");
        options.Input.InputPath = json;
        var document = new AnalysisExecutor().Execute(options).Result;
        Assert.Equal(2, jsonl.TotalObjects);
        Assert.Equal(1, Assert.Single(jsonl.FieldStatistics).MinNumber);
        Assert.Equal(3, Assert.Single(jsonl.FieldStatistics).MaxNumber);
        Assert.Equal(jsonl.TotalObjects, document.TotalObjects);
        Assert.Equal(Assert.Single(jsonl.FieldStatistics).AvgNumber, Assert.Single(document.FieldStatistics).AvgNumber);
        Assert.Equal(2, jsonl.SamplingConfidence!.EffectiveSampleSize);
        Assert.Equal(3, jsonl.SamplingConfidence.EstimatedTotal);
        Assert.Equal(2d / 3, jsonl.SamplingConfidence.SamplingFraction);
    }

    [Fact]
    public void CancellationInsideWorker_StopsWithinChunkAndReleasesInput()
    {
        var options = Input("worker-cancel", string.Concat(Enumerable.Repeat("{\"x\":1}\n", 25000)));
        using var cancellation = new CancellationTokenSource();
        var analyzer = new CancellingAnalyzer(cancellation);
        var events = new List<AnalysisEvent>();
        Assert.ThrowsAny<OperationCanceledException>(() => new AnalysisExecutor().Execute(options,
            runContext: new AnalysisRunContext
            {
                Analyzers = FieldAnalyzerRegistry.Default.With(analyzer), EventSink = events.Add
            }, cancellationToken: cancellation.Token));
        Assert.Equal(1, analyzer.Observed);
        Assert.Equal(AnalysisEventKind.Cancelled, Assert.Single(events, IsTerminal).Kind);
        using var exclusive = new FileStream(options.Input.InputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void InvalidInput_ReleasesFileAndReportsInputFailure()
    {
        var options = Input("invalid", "{\"x\":1}\n{\n");
        var events = new List<AnalysisEvent>();
        Assert.ThrowsAny<Exception>(() => new AnalysisExecutor().Execute(options,
            runContext: new AnalysisRunContext { EventSink = events.Add }));
        var failed = Assert.Single(events, IsTerminal);
        Assert.Equal(AnalysisEventKind.Failed, failed.Kind);
        Assert.Equal(AnalysisFailureKind.Input, failed.Failure);
        using var exclusive = new FileStream(options.Input.InputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void InvalidConfiguration_ReportsOneStartedAndOneFailure()
    {
        var options = Input("config", "{}\n");
        options.Parse.MaxDegreeOfParallelism = 0;
        var events = new List<AnalysisEvent>();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalysisExecutor().Execute(options,
            runContext: new AnalysisRunContext { EventSink = events.Add }));
        Assert.Equal(AnalysisEventKind.Started, events[0].Kind);
        Assert.Equal(AnalysisFailureKind.Configuration, Assert.Single(events, IsTerminal).Failure);
    }

    [Fact]
    public void RunContext_IsSingleUse()
    {
        var options = Input("single", "{}\n");
        var context = new AnalysisRunContext();
        var executor = new AnalysisExecutor();
        executor.Execute(options, runContext: context);
        Assert.Throws<InvalidOperationException>(() => executor.Execute(options, runContext: context));
    }

    [Fact]
    public async Task CommandRunners_IsolateHostServices()
    {
        var first = Input("command-a", "{\"x\":1}\n");
        var second = Input("command-b", "{\"y\":2}\n");
        var consoleA = new RecordingConsole();
        var consoleB = new RecordingConsole();
        first.Report.OutputPath = Path.Combine(_directory, "a.json");
        second.Report.OutputPath = Path.Combine(_directory, "b.json");
        var runnerA = new AnalysisCommandRunner(new AnalysisServices { Console = consoleA });
        var runnerB = new AnalysisCommandRunner(new AnalysisServices { Console = consoleB });
        var codes = await Task.WhenAll(Task.Run(() => runnerA.Run(first)), Task.Run(() => runnerB.Run(second)));
        Assert.All(codes, code => Assert.Equal(0, code));
        Assert.Contains(consoleA.Messages, message => message.Contains("a.json", StringComparison.Ordinal));
        Assert.DoesNotContain(consoleA.Messages, message => message.Contains("b.json", StringComparison.Ordinal));
        Assert.Contains(consoleB.Messages, message => message.Contains("b.json", StringComparison.Ordinal));
        Assert.DoesNotContain(consoleB.Messages, message => message.Contains("a.json", StringComparison.Ordinal));
    }

    private sealed class InlineProgress(Action<long> action) : IProgress<long>
    { public void Report(long value) => action(value); }

    private sealed class BrokenReporter : IProgressReporter
    {
        public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
            => throw new OperationCanceledException("unrelated token");
    }

    private sealed class CancellingAnalyzer(CancellationTokenSource cancellation) : IFieldAnalyzer
    {
        public int Observed;
        public string Id => "cancel-test";
        public int Version => 1;
        public IReadOnlyCollection<RecordValueKind> InputKinds { get; } = [RecordValueKind.Number];
        public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) => new State(this, cancellation);
        public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data) => throw new NotSupportedException();

        private sealed class State(CancellingAnalyzer owner, CancellationTokenSource cancellation) : IFieldAnalyzerState
        {
            public void Observe(RecordValue value) { Interlocked.Increment(ref owner.Observed); cancellation.Cancel(); }
            public void Merge(IFieldAnalyzerState other) { }
            public void Complete(FieldStatistic result) { }
            public JsonElement Capture() => throw new NotSupportedException();
        }
    }

    private sealed class RecordingConsole : IAnalysisConsole
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public void MarkupLine(string markup) => Messages.Enqueue(markup);
        public void WriteLine(string text = "") => Messages.Enqueue(text);
        public void Write(string text) => Messages.Enqueue(text);
    }

    private sealed class ThrowingObserver : IAnalysisExecutionObserver
    {
        public int Started { get; private set; }
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        public void OnStarted() { Started++; throw new InvalidOperationException("start"); }
        public void OnCompleted(PipelineResult result) { Completed++; throw new InvalidOperationException("complete"); }
        public void OnCancelled() => throw new InvalidOperationException("cancel");
        public void OnFailed(Exception exception) { Failed++; throw new InvalidOperationException("fail"); }
    }
}
