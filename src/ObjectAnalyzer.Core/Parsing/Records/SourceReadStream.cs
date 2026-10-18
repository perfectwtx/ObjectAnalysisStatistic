namespace ObjectAnalyzer.Parsing;

/// <summary>Non-owning stream view: parser disposal cannot close caller-owned input; reads observe cancellation.</summary>
internal sealed class SourceReadStream(Stream inner, CancellationToken cancellationToken, long? maxBytes = null, Action? checkMemory = null) : Stream
{
    private long _read;
    private void BeforeRead() { cancellationToken.ThrowIfCancellationRequested(); checkMemory?.Invoke(); }
    private int AfterRead(int count)
    {
        _read = checked(_read + count);
        if (maxBytes.HasValue && _read > maxBytes.Value) throw new AnalysisResourceLimitException("buffered-input-bytes", maxBytes.Value);
        checkMemory?.Invoke(); return count;
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count)
    { BeforeRead(); return AfterRead(inner.Read(buffer, offset, count)); }
    public override int Read(Span<byte> buffer)
    { BeforeRead(); return AfterRead(inner.Read(buffer)); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        BeforeRead();
        if (!cancellationToken.CanBeCanceled) return AfterRead(await inner.ReadAsync(buffer, token).ConfigureAwait(false));
        if (!token.CanBeCanceled || token == cancellationToken) return AfterRead(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
        return AfterRead(await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false));
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
