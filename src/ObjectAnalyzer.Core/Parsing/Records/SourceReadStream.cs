namespace ObjectAnalyzer.Parsing;

/// <summary>Non-owning stream view: parser disposal cannot close caller-owned input; reads observe cancellation.</summary>
internal sealed class SourceReadStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count)
    { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer)
    { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (!cancellationToken.CanBeCanceled) return await inner.ReadAsync(buffer, token).ConfigureAwait(false);
        if (!token.CanBeCanceled || token == cancellationToken) return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
        return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
