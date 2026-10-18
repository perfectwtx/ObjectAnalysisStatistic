using System.Collections.Immutable;

namespace ObjectAnalyzer.Parsing;

internal sealed class RegisteredParser(IRecordParser inner) : IRecordParser
{
    internal IRecordParser Inner { get; } = inner;
    public IReadOnlyList<string> Formats { get; } = inner.Formats.ToImmutableArray();
    public IReadOnlyList<string> Extensions { get; } = inner.Extensions.ToImmutableArray();
    public SourceCapabilities Capabilities { get; } = inner.Capabilities;
    private readonly ImmutableDictionary<string, SourceCapabilities> _capabilities = inner.Formats.ToImmutableDictionary(format => format, inner.GetCapabilities, StringComparer.OrdinalIgnoreCase);
    public SourceCapabilities GetCapabilities(string format) => _capabilities[format];
    public IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
        => Inner.Parse(stream, source, options, cancellationToken);
    public IAsyncEnumerable<SourceRecord> ParseAsync(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
        => Inner.ParseAsync(stream, source, options, cancellationToken);
}
