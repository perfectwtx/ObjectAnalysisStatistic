using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Tests;

public sealed class DataSourceContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oa-sources-" + Guid.NewGuid().ToString("N"));
    public DataSourceContractTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string Write(string name, string content)
    { string path = Path.Combine(_directory, name); File.WriteAllText(path, content); return path; }
    private static RecordValue Value(string json)
    { using var document = JsonDocument.Parse(json); return RecordValue.FromJsonElement(document.RootElement); }
    private static AnalysisOptions Options() => new() { Parse = { MaxDegreeOfParallelism = 1 } };
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static bool Terminal(AnalysisEvent item) => item.Kind is AnalysisEventKind.Completed or AnalysisEventKind.Failed or AnalysisEventKind.Cancelled;

    [Fact]
    public async Task FileStreamSyncAndAsyncRecords_ProduceEquivalentTypedResults()
    {
        const string row = "{\"a.b\":9007199254740993,\"a\":{\"b\":\"1\"},\"items\":[1,null],\"empty\":{}}";
        string path = Write("exact.jsonl", row + "\n");
        var executor = new AnalysisExecutor();
        var fromFile = executor.Execute(new AnalysisOptions { Input = { InputPath = path }, Parse = { MaxDegreeOfParallelism = 1 } }).Result;
        var fromStream = executor.Execute(Options(), runContext: new AnalysisRunContext
        { Source = DataSources.Stream(() => new MemoryStream(Bytes(row)), "jsonl") }).Result;
        var fromRecords = executor.Execute(Options(), runContext: new AnalysisRunContext
        { Source = DataSources.Records([Value(row)]) }).Result;
        var fromAsync = (await executor.ExecuteAsync(Options(), runContext: new AnalysisRunContext
        { Source = DataSources.AsyncRecords(AsyncValues([Value(row)])) })).Result;
        string expected = JsonSerializer.Serialize(fromFile);
        Assert.Equal(expected, JsonSerializer.Serialize(fromStream));
        Assert.Equal(expected, JsonSerializer.Serialize(fromRecords));
        Assert.Equal(expected, JsonSerializer.Serialize(fromAsync));
        var literal = fromFile.FieldStatistics.Single(field => field.Path.Equals(FieldPath.Property("a.b")));
        Assert.Contains(RecordValue.FromObject(9007199254740993L), literal.ValueCounts.Keys);
        Assert.Contains(fromFile.FieldStatistics, field => field.FieldName == "a.b" && field.PrimaryType == "String");
    }

    [Fact]
    public void Capabilities_ReportBufferingResumeAndOwnershipRequirements()
    {
        var json = DataSources.File(Write("data.json", "[]")).Descriptor;
        var jsonl = DataSources.File(Write("data.jsonl", "{}\n")).Descriptor;
        Assert.False(json.Capabilities.Streaming);
        Assert.True(json.Capabilities.RootProjection);
        Assert.True(jsonl.Capabilities.Streaming);
        Assert.True(jsonl.Capabilities.CanResume);
        Assert.Equal(SourcePositionKind.PhysicalLine, jsonl.Capabilities.PositionKind);
        Assert.False(DataSources.Stream(new MemoryStream(), "jsonl").Descriptor.Capabilities.CanResume);
        Assert.True(RecordParserRegistry.Default.Resolve("excel").Capabilities.RequiresSeek);
        Assert.False(RecordParserRegistry.Default.Resolve("xml").Capabilities.Streaming);
        Assert.False(RecordParserRegistry.Default.Resolve("yaml").Capabilities.CanSkipMalformed);
    }

    [Fact]
    public void ParserAndDetectorRegistration_IsLocalAndMetadataIsFrozen()
    {
        var formats = new List<string> { "pipe" };
        var registry = RecordParserRegistry.Default.With(new PipeParser(formats))
            .WithDetector(path => path.EndsWith(".custom", StringComparison.Ordinal) ? "pipe" : null);
        formats[0] = "changed";
        Assert.NotNull(registry.Resolve("pipe"));
        Assert.Throws<NotSupportedException>(() => RecordParserRegistry.Default.Resolve("pipe"));
        var options = Options();
        options.Input.InputPath = Write("input.custom", "value|7");
        var context = new AnalysisRunContext { Parsers = registry };
        var result = new AnalysisExecutor().Execute(options, runContext: context).Result;
        Assert.Equal("pipe", context.SourceDescriptor!.Format);
        Assert.Equal(7, Assert.Single(result.FieldStatistics).AvgNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamOwnership_IsHonoredOnEarlyStop(bool leaveOpen)
    {
        var stream = new TrackingStream(Bytes("{}\n{}\n"));
        var source = DataSources.Stream(stream, "jsonl", leaveOpen: leaveOpen);
        using (var iterator = source.Read(new SourceReadOptions()).GetEnumerator()) Assert.True(iterator.MoveNext());
        Assert.Equal(leaveOpen ? 0 : 1, stream.Disposals);
        Assert.Throws<InvalidOperationException>(() => source.Read(new SourceReadOptions()).ToList());
        if (leaveOpen) { Assert.True(stream.CanRead); stream.Dispose(); }
    }

    [Fact]
    public async Task NativeAsyncParser_UsesAsyncReadsAndReleasesStream()
    {
        var stream = new AsyncOnlyStream(Bytes("{\"x\":1}\n{\"x\":2}\n"));
        var source = DataSources.Stream(stream, "jsonl");
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(new SourceReadOptions())) records.Add(record);
        Assert.Equal(2, records.Count);
        Assert.True(stream.AsyncReads > 0);
        Assert.Equal(1, stream.Disposals);
        Assert.Equal(2, records[1].Location.Line);
    }

    [Fact]
    public void SkipPolicy_PreservesPhysicalLinesAndBoundsDiagnostics()
    {
        string path = Write("bad.jsonl", "{\"x\":1}\n{\n5\n\n{\"x\":2}\n");
        var diagnostics = new List<SourceDiagnostic>();
        long position = 0;
        var source = DataSources.File(path);
        var records = source.Read(new SourceReadOptions
        {
            MalformedRecords = MalformedRecordPolicy.Skip, DiagnosticLimit = 1,
            DiagnosticSink = diagnostics.Add, PositionProgress = value => position = value
        }).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(1, records[0].Location.Line);
        Assert.Equal(5, records[1].Location.Line);
        Assert.Equal(5, position);
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(2, diagnostics[^1].Occurrences);
        Assert.Equal(2, diagnostics[0].Location.Line);
        var resumed = source.Read(new SourceReadOptions { StartPosition = 3 }).ToList();
        Assert.Equal(5, Assert.Single(resumed).Location.Line);
        var failure = Assert.Throws<SourceReadException>(() => source.Read(new SourceReadOptions()).ToList());
        Assert.Equal(2, failure.Location.Line);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void UnsupportedSkipPolicy_IsRejectedBeforeOpeningInput()
    {
        int opens = 0;
        var source = DataSources.Stream(() => { opens++; return new MemoryStream(Bytes("[]")); }, "json");
        var options = Options();
        options.Parse.MalformedRecords = MalformedRecordPolicy.Skip;
        Assert.Throws<NotSupportedException>(() => new AnalysisExecutor().Execute(options,
            runContext: new AnalysisRunContext { Source = source }));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Pipeline_SkipFilterAndSampleShareOnePopulationAndCursor()
    {
        string path = Write("sample.jsonl", "{\"x\":0}\n{\n{\"x\":1}\n5\n{\"x\":2}\n{\"x\":3}\n");
        var options = Options();
        options.Input.InputPath = path;
        options.Parse.MalformedRecords = MalformedRecordPolicy.Skip;
        options.FieldSelection.Filter = "x > 0";
        options.Sampling.SampleStep = 2;
        var context = new AnalysisRunContext();
        var result = (await new AnalysisExecutor().ExecuteAsync(options, runContext: context)).Result;
        Assert.Equal(2, result.TotalObjects);
        Assert.Equal(2, context.SkippedInputRecords);
        Assert.Equal(6, context.LastSourcePosition);
        Assert.Equal(3, result.SamplingConfidence!.EstimatedTotal);
        Assert.Equal(1, Assert.Single(result.FieldStatistics).MinNumber);
        Assert.Equal(3, Assert.Single(result.FieldStatistics).MaxNumber);
    }

    [Fact]
    public async Task CompressedRootProjection_UsesSameInputContract()
    {
        string path = Path.Combine(_directory, "root.json.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress)) gzip.Write(Bytes("{\"data\":[{\"x\":1},{\"x\":2}]}"));
        var options = Options();
        options.Input.InputPath = path;
        options.Input.RootPath = "$.data[*]";
        var executor = new AnalysisExecutor();
        Assert.Equal(2, executor.Execute(options).Result.TotalObjects);
        Assert.Equal(2, (await executor.ExecuteAsync(options)).Result.TotalObjects);
        Assert.False(DataSources.File(path).Descriptor.Capabilities.Streaming);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task AsyncCancellation_StopsProducerAndReleasesItsEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        int produced = 0;
        bool disposed = false;
        async IAsyncEnumerable<RecordValue> Generate([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                for (int i = 0; i < 100000; i++)
                {
                    token.ThrowIfCancellationRequested();
                    produced++;
                    yield return Value("{\"x\":1}");
                    if (i % 64 == 0) await Task.Yield();
                }
            }
            finally { disposed = true; }
        }
        var analyzer = new ActionAnalyzer(cancellation.Cancel);
        var events = new List<AnalysisEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisExecutor().ExecuteAsync(Options(),
            runContext: new AnalysisRunContext
            {
                Source = DataSources.AsyncRecords(Generate()),
                Analyzers = FieldAnalyzerRegistry.Default.With(analyzer), EventSink = events.Add
            }, cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(disposed);
        Assert.InRange(produced, 1, 10200);
        Assert.Equal(AnalysisEventKind.Cancelled, Assert.Single(events, Terminal).Kind);
    }

    [Fact]
    public async Task ConsumerFailure_StopsAsyncProducerWithoutExternalCancellation()
    {
        bool disposed = false;
        int produced = 0;
        var row = Value("{\"x\":1}");
        async IAsyncEnumerable<RecordValue> Generate([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                for (int i = 0; i < 100000; i++)
                {
                    token.ThrowIfCancellationRequested();
                    produced++;
                    yield return row;
                    if (i % 64 == 0) await Task.Yield();
                }
            }
            finally { disposed = true; }
        }
        var events = new List<AnalysisEvent>();
        var task = new AnalysisExecutor().ExecuteAsync(Options(), runContext: new AnalysisRunContext
        {
            Source = DataSources.AsyncRecords(Generate()), EventSink = events.Add,
            Analyzers = FieldAnalyzerRegistry.Default.With(new ActionAnalyzer(() => throw new InvalidOperationException("consumer failed")))
        });
        await Assert.ThrowsAsync<AggregateException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(disposed);
        Assert.InRange(produced, 1, 10200);
        Assert.Equal(AnalysisEventKind.Failed, Assert.Single(events, Terminal).Kind);
    }

    [Fact]
    public async Task InvalidAsyncConfiguration_StillEmitsOneTerminalEvent()
    {
        var options = Options();
        options.Parse.MaxDegreeOfParallelism = 0;
        var events = new List<AnalysisEvent>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AnalysisExecutor().ExecuteAsync(options,
            runContext: new AnalysisRunContext { EventSink = events.Add }));
        Assert.Equal(AnalysisEventKind.Started, events[0].Kind);
        Assert.Equal(AnalysisEventKind.Failed, Assert.Single(events, Terminal).Kind);
    }

    [Fact]
    public async Task Database_SyncAndAsyncRecordsPreserveTypesAndRejectCursor()
    {
        var source = DataSources.Database("Data Source=:memory:;Pooling=False", DatabaseProvider.Sqlite,
            "SELECT 9007199254740993 AS 'a.b', X'0102' AS blob, NULL AS absent UNION ALL SELECT 2, X'0304', NULL");
        var sync = source.Read(new SourceReadOptions()).ToList();
        var asynchronous = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(new SourceReadOptions())) asynchronous.Add(record);
        Assert.Equal(2, sync.Count);
        Assert.Equal(sync[0].Fields, asynchronous[0].Fields);
        Assert.Equal(RecordValueKind.Binary, sync[0].Fields[FieldPath.Property("blob")].Kind);
        Assert.Equal(RecordValue.FromObject(9007199254740993L), sync[0].Fields[FieldPath.Property("a.b")]);
        Assert.Equal(2, sync[1].Location.Row);
        Assert.Throws<NotSupportedException>(() => source.Read(new SourceReadOptions { StartPosition = 1 }).ToList());
    }

    [Theory]
    [InlineData("[{\"x\":1}")]
    [InlineData("[{\"x\":1},]")]
    [InlineData("{\"x\":1} garbage")]
    public async Task MalformedDocuments_FailWithLocationInBothModes(string content)
    {
        var source = DataSources.Stream(() => new MemoryStream(Bytes(content)), "json");
        var sync = Assert.Throws<SourceReadException>(() => source.Read(new SourceReadOptions()).ToList());
        Assert.Equal("stream", sync.Location.Source);
        await Assert.ThrowsAsync<SourceReadException>(async () =>
        { await foreach (var unused in source.ReadAsync(new SourceReadOptions())) { } });
    }

    [Fact]
    public void XmlAndYaml_RespectSameFlattenAndLiteralPathSemantics()
    {
        var settings = new SourceReadOptions();
        var json = DataSources.Stream(new MemoryStream(Bytes("[{\"a.b\":\"x\",\"nested\":{\"child\":\"v\"}}]")), "json").Read(settings).Single();
        var xml = DataSources.Stream(new MemoryStream(Bytes("<rows><row><a.b>x</a.b><nested><child>v</child></nested></row></rows>")), "xml").Read(settings).Single();
        var yaml = DataSources.Stream(new MemoryStream(Bytes("- a.b: x\n  nested:\n    child: v\n")), "yaml").Read(settings).Single();
        Assert.Equal(json.Fields.OrderBy(pair => pair.Key.ToString()), xml.Fields.OrderBy(pair => pair.Key.ToString()));
        Assert.Equal(json.Fields.OrderBy(pair => pair.Key.ToString()), yaml.Fields.OrderBy(pair => pair.Key.ToString()));
    }

    [Fact]
    public void Csv_StreamsFromNonSeekableInputWithPhysicalRowLocations()
    {
        var source = DataSources.Stream(new ForwardStream(Bytes("name,age\na,1\nb,2\n")), "csv");
        Assert.True(source.Descriptor.Capabilities.Streaming);
        Assert.False(source.Descriptor.Capabilities.RequiresSeek);
        var rows = source.Read(new SourceReadOptions()).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[1].Location.Row);
        Assert.Equal(RecordValueKind.Number, rows[0].Fields[FieldPath.Property("age")].Kind);
    }

    [Fact]
    public void SparseDenseHllMerge_PreservesPreviouslyObservedRegisters()
    {
        var sparse = new HyperLogLog();
        var dense = new HyperLogLog();
        var all = new HyperLogLog();
        for (int i = 0; i < 200; i++) { sparse.Add("v" + i); all.Add("v" + i); }
        for (int i = 200; i < 2200; i++) { dense.Add("v" + i); all.Add("v" + i); }
        sparse.Merge(dense);
        Assert.Equal(all.Estimate(), sparse.Estimate());
    }

    private static async IAsyncEnumerable<RecordValue> AsyncValues(IEnumerable<RecordValue> records)
    { await Task.Yield(); foreach (var record in records) yield return record; }
    private sealed class PipeParser(List<string> formats) : IRecordParser
    {
        public IReadOnlyList<string> Formats => formats;
        public IReadOnlyList<string> Extensions { get; } = [".pipe"];
        public SourceCapabilities Capabilities { get; } = new(true, true, false, false, false, false, SourcePositionKind.Record);
        public IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string[] parts = reader.ReadToEnd().Split('|');
            yield return SourceRecord.FromValue(RecordValue.FromObject(new Dictionary<string, object?> { [parts[0]] = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) }), new SourceLocation(source, 1), options.Flatten);
        }
    }
    private class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class AsyncOnlyStream(byte[] bytes) : TrackingStream(bytes)
    {
        private readonly byte[] _content = bytes;
        public int AsyncReads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read was used.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous read was used.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { AsyncReads++; cancellationToken.ThrowIfCancellationRequested(); int count = (int)Math.Min(buffer.Length, Length - Position); _content.AsMemory((int)Position, count).CopyTo(buffer); Position += count; return ValueTask.FromResult(count); }
    }
    private sealed class ForwardStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
    private sealed class ActionAnalyzer(Action action) : IFieldAnalyzer
    {
        public string Id => "source-action-test";
        public int Version => 1;
        public IReadOnlyCollection<RecordValueKind> InputKinds { get; } = [RecordValueKind.Number];
        public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) => new State(action);
        public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data) => throw new NotSupportedException();
        private sealed class State(Action action) : IFieldAnalyzerState
        {
            public void Observe(RecordValue value) => action();
            public void Merge(IFieldAnalyzerState other) { }
            public void Complete(FieldStatistic result) { }
            public JsonElement Capture() => throw new NotSupportedException();
        }
    }
}
