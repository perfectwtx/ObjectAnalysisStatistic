using System.Threading.Channels;

namespace ObjectAnalyzer.Parsing;

/// <summary>Bounded bridge between asynchronous input and the synchronous CPU pipeline.</summary>
internal sealed class AsyncRecordBridge(IAnalysisDataSource source) : IAnalysisDataSource
{
    public SourceDescriptor Descriptor => source.Descriptor;
    public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<SourceRecord>(new BoundedChannelOptions(options.ResourceLimits.AsyncQueueCapacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await foreach (var record in source.ReadAsync(options, lifetime.Token).WithCancellation(lifetime.Token).ConfigureAwait(false))
                {
                    global::ObjectAnalyzer.Analysis.ObjectAnalyzer.EstimateRecordBytes(record.ToEngineRecord(), options.ResourceLimits);
                    options.CheckMemory?.Invoke();
                    await channel.Writer.WriteAsync(record, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) { failure = exception; }
            finally { channel.Writer.TryComplete(failure); }
        }, CancellationToken.None);
        try
        {
            while (channel.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                while (channel.Reader.TryRead(out var record))
                { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
        }
        finally
        {
            lifetime.Cancel();
            producer.GetAwaiter().GetResult();
        }
    }
}
