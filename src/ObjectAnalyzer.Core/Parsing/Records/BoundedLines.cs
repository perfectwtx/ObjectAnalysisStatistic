using System.Runtime.CompilerServices;
using System.Text;

namespace ObjectAnalyzer.Parsing;

/// <summary>Incremental line framing: rejects oversized physical lines before ReadLine can allocate them.</summary>
internal static class BoundedLines
{
    private sealed class Frame(int maximum)
    {
        private readonly StringBuilder _text = new();
        private bool _afterCr;
        public string? Push(char ch)
        {
            if (_afterCr) { _afterCr = false; if (ch == '\n') return null; }
            if (ch is '\r' or '\n')
            { string line = _text.ToString(); _text.Clear(); _afterCr = ch == '\r'; return line; }
            if (_text.Length >= maximum) throw new AnalysisResourceLimitException("record-characters", maximum);
            _text.Append(ch); return null;
        }
        public string? Finish() => _text.Length > 0 ? _text.ToString() : null;
    }
    public static IEnumerable<string> Read(TextReader reader, int maximum, CancellationToken cancellationToken)
    {
        var frame = new Frame(maximum); var buffer = new char[4096]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i++) if (frame.Push(buffer[i]) is { } line) yield return line;
        }
        if (frame.Finish() is { } last) yield return last;
    }
    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader, int maximum,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frame = new Frame(maximum); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            for (int i = 0; i < count; i++) if (frame.Push(buffer[i]) is { } line) yield return line;
        if (frame.Finish() is { } last) yield return last;
    }
}
