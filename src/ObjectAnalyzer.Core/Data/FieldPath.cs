using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Data;

public readonly record struct FieldPathSegment
{
    public string? Name { get; }
    public int? Index { get; }
    public bool IsIndex => Index.HasValue;
    private FieldPathSegment(string? name, int? index) { Name = name; Index = index; }
    public static FieldPathSegment Property(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), null);
    public static FieldPathSegment Element(int index) => index >= 0 ? new(null, index) : throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>Case-sensitive structural path. Unsafe/empty property names use JSON quoted brackets.</summary>
[JsonConverter(typeof(FieldPathJsonConverter))]
public sealed class FieldPath : IEquatable<FieldPath>
{
    public ImmutableArray<FieldPathSegment> Segments { get; }
    public static FieldPath Root { get; } = new([]);
    private FieldPath(ImmutableArray<FieldPathSegment> segments) => Segments = segments;
    public FieldPath AppendProperty(string name) => new(Segments.Add(FieldPathSegment.Property(name)));
    public FieldPath AppendIndex(int index) => new(Segments.Add(FieldPathSegment.Element(index)));
    public FieldPath Parent => Segments.Length == 0 ? this : new(Segments.RemoveAt(Segments.Length - 1));
    public static FieldPath Property(string name) => Root.AppendProperty(name);
    public bool IsPrefixOf(FieldPath other) => Segments.Length <= other.Segments.Length && Segments.SequenceEqual(other.Segments.Take(Segments.Length));

    /// <summary>Split a comma-separated selection without splitting commas inside quoted property names.</summary>
    public static IReadOnlyList<FieldPath> ParseList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paths = new List<FieldPath>();
        bool quoted = false;
        bool escaped = false;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (!quoted && text[i] == ','))
            {
                string part = text[start..i].Trim();
                if (part.Length > 0) paths.Add(Parse(part));
                start = i + 1;
                continue;
            }
            char c = text[i];
            if (quoted && !escaped && c == '\\') { escaped = true; continue; }
            if (!escaped && c == '"') quoted = !quoted;
            escaped = false;
        }
        if (quoted) throw new FormatException("Unterminated quoted field name.");
        return paths;
    }

    public static FieldPath Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = Root;
        int position = 0;
        while (position < text.Length)
        {
            if (text[position] == '[')
            {
                position++;
                if (position < text.Length && text[position] == '"')
                {
                    int start = position++;
                    bool escaped = false;
                    while (position < text.Length)
                    {
                        char c = text[position++];
                        if (!escaped && c == '"') break;
                        if (!escaped && c == '\\') escaped = true; else escaped = false;
                    }
                    if (position >= text.Length || text[position] != ']') throw new FormatException($"Invalid field path: {text}");
                    result = result.AppendProperty(JsonSerializer.Deserialize<string>(text[start..position])!);
                    position++;
                }
                else
                {
                    int close = text.IndexOf(']', position);
                    if (close < 0 || !int.TryParse(text.AsSpan(position, close - position), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                        throw new FormatException($"Invalid array index in field path: {text}");
                    result = result.AppendIndex(index);
                    position = close + 1;
                }
            }
            else
            {
                int start = position;
                while (position < text.Length && text[position] is not ('.' or '[' or ']')) position++;
                if (position == start) throw new FormatException($"Invalid field path: {text}");
                result = result.AppendProperty(text[start..position]);
            }
            if (position == text.Length) break;
            if (text[position] == '.')
            {
                if (++position == text.Length || text[position] is '.' or '[' or ']') throw new FormatException($"Invalid field path: {text}");
            }
            else if (text[position] != '[') throw new FormatException($"Invalid field path: {text}");
        }
        return result;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (segment.IsIndex) builder.Append('[').Append(segment.Index!.Value.ToString(CultureInfo.InvariantCulture)).Append(']');
            else
            {
                string name = segment.Name!;
                if (name.Length == 0 || name.Any(c => c is '.' or '[' or ']' or '\\' or '"' or ',' or '\'' or '(' or ')' or '=' or '!' or '<' or '>' or '&' or '|' || char.IsWhiteSpace(c) || char.IsControl(c)))
                    builder.Append('[').Append(JsonSerializer.Serialize(name)).Append(']');
                else { if (builder.Length > 0) builder.Append('.'); builder.Append(name); }
            }
        }
        return builder.ToString();
    }
    public bool Equals(FieldPath? other) => other is not null && Segments.SequenceEqual(other.Segments);
    public override bool Equals(object? obj) => obj is FieldPath other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var segment in Segments) hash.Add(segment); return hash.ToHashCode(); }
    public static bool operator ==(FieldPath? left, FieldPath? right) => Equals(left, right);
    public static bool operator !=(FieldPath? left, FieldPath? right) => !Equals(left, right);
}

public sealed class FieldPathJsonConverter : JsonConverter<FieldPath>
{
    public override FieldPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => FieldPath.Parse(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, FieldPath value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    public override FieldPath ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => FieldPath.Parse(reader.GetString()!);
    public override void WriteAsPropertyName(Utf8JsonWriter writer, FieldPath value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());
}
