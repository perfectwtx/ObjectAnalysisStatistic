using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Data;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These names identify data kinds, matching JSON and CLR terminology.")]
public enum RecordValueKind { Null, Boolean, Number, String, DateTime, DateTimeOffset, Date, Time, Duration, Guid, Binary, Object, Array }

/// <summary>
/// Immutable, culture-independent value identity. Numerically equal CLR numeric values share
/// an identity; strings never share an identity with numbers, nulls or structured values.
/// Object member order is insignificant; array order and property-name casing are significant.
/// </summary>
[JsonConverter(typeof(RecordValueJsonConverter))]
public sealed class RecordValue : IEquatable<RecordValue>, IComparable<RecordValue>
{
    private static readonly JsonSerializerOptions IdentityOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 512
    };
    private string? _canonicalKey;
    public RecordValueKind Kind { get; }
    public string Scalar { get; }
    public ImmutableArray<RecordValue> Items { get; }
    public ImmutableSortedDictionary<string, RecordValue> Properties { get; }
    public static RecordValue Null { get; } = new(RecordValueKind.Null, "");

    private RecordValue(RecordValueKind kind, string scalar,
        ImmutableArray<RecordValue> items = default,
        ImmutableSortedDictionary<string, RecordValue>? properties = null)
    {
        Kind = kind;
        Scalar = scalar;
        Items = items.IsDefault ? [] : items;
        Properties = properties ?? ImmutableSortedDictionary.Create<string, RecordValue>(StringComparer.Ordinal);
    }

    public static RecordValue FromString(string value) => new(RecordValueKind.String, value ?? throw new ArgumentNullException(nameof(value)));
    public static implicit operator RecordValue(string value) => FromString(value);
    public static RecordValue FromObject(object? value) => FromObject(value, 0);

    private static RecordValue FromObject(object? value, int depth)
    {
        if (depth > 128) throw new InvalidDataException("Record nesting exceeds 128 levels.");
        return value switch
        {
            null or DBNull => Null,
            RecordValue typed => typed,
            JsonElement json => FromJsonElement(json, depth),
            JsonNode node => FromJsonElement(JsonSerializer.SerializeToElement(node), depth),
            string text => FromString(text),
            char character => FromString(character.ToString()),
            bool boolean => new(RecordValueKind.Boolean, boolean ? "true" : "false"),
            byte or sbyte or short or ushort or int or uint or long or ulong or BigInteger or decimal =>
                Number(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)),
            double number => Number(number.ToString("R", CultureInfo.InvariantCulture)),
            float number => Number(number.ToString("R", CultureInfo.InvariantCulture)),
            DateTime date => new(RecordValueKind.DateTime, date.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset date => new(RecordValueKind.DateTimeOffset, date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            DateOnly date => new(RecordValueKind.Date, date.ToString("O", CultureInfo.InvariantCulture)),
            TimeOnly time => new(RecordValueKind.Time, time.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan duration => new(RecordValueKind.Duration, duration.ToString("c", CultureInfo.InvariantCulture)),
            Guid guid => new(RecordValueKind.Guid, guid.ToString("D")),
            byte[] bytes => new(RecordValueKind.Binary, Convert.ToBase64String(bytes)),
            IEnumerable<KeyValuePair<string, object?>> map => Object(map.Select(p => KeyValuePair.Create(p.Key, FromObject(p.Value, depth + 1)))),
            IEnumerable<KeyValuePair<string, RecordValue>> map => Object(map),
            IDictionary map => Object(map.Cast<DictionaryEntry>().Select(p => KeyValuePair.Create(
                p.Key as string ?? throw new NotSupportedException("Record property names must be strings."), FromObject(p.Value, depth + 1)))),
            IEnumerable sequence => new(RecordValueKind.Array, "", sequence.Cast<object?>().Select(v => FromObject(v, depth + 1)).ToImmutableArray()),
            _ => throw new NotSupportedException($"Unsupported record value type: {value.GetType().FullName}")
        };
    }

    public static RecordValue FromJsonElement(JsonElement value) => FromJsonElement(value, 0);
    private static RecordValue FromJsonElement(JsonElement value, int depth)
    {
        if (depth > 128) throw new InvalidDataException("Record nesting exceeds 128 levels.");
        return value.ValueKind switch
        {
            JsonValueKind.Null => Null,
            JsonValueKind.String => FromString(value.GetString()!),
            JsonValueKind.Number => Number(value.GetRawText()),
            JsonValueKind.True => new(RecordValueKind.Boolean, "true"),
            JsonValueKind.False => new(RecordValueKind.Boolean, "false"),
            JsonValueKind.Array => new(RecordValueKind.Array, "", value.EnumerateArray().Select(v => FromJsonElement(v, depth + 1)).ToImmutableArray()),
            JsonValueKind.Object => Object(value.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, FromJsonElement(p.Value, depth + 1)))),
            _ => throw new JsonException("Undefined is not a record value.")
        };
    }

    private static RecordValue Object(IEnumerable<KeyValuePair<string, RecordValue>> properties) =>
        new(RecordValueKind.Object, "", properties: properties.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static RecordValue Number(string value) => new(RecordValueKind.Number, NormalizeNumber(value));

    // A decimal coefficient/exponent representation avoids rounding through double or decimal.
    private static string NormalizeNumber(string value)
    {
        if (value is "NaN" or "Infinity" or "-Infinity") return value;
        int separator = value.IndexOfAny(['e', 'E']);
        BigInteger exponent = separator < 0 ? BigInteger.Zero : BigInteger.Parse(value.AsSpan(separator + 1), CultureInfo.InvariantCulture);
        string coefficient = separator < 0 ? value : value[..separator];
        bool negative = coefficient.StartsWith('-');
        if (negative) coefficient = coefficient[1..];
        int point = coefficient.IndexOf('.');
        if (point >= 0) { exponent -= coefficient.Length - point - 1; coefficient = coefficient.Remove(point, 1); }
        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length == 0) return "0";
        int length = coefficient.Length;
        coefficient = coefficient.TrimEnd('0');
        exponent += length - coefficient.Length;
        string sign = negative ? "-" : "";
        if (exponent >= 0 && exponent + coefficient.Length <= 28)
            return sign + coefficient + new string('0', (int)exponent);
        if (exponent < 0 && exponent >= -28)
        {
            int position = coefficient.Length + (int)exponent;
            return sign + (position > 0 ? coefficient.Insert(position, ".") : "0." + new string('0', -position) + coefficient);
        }
        return sign + coefficient + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    public bool IsInteger => Kind == RecordValueKind.Number && Scalar is not ("NaN" or "Infinity" or "-Infinity")
        && !Scalar.Contains('.') && !Scalar.Contains("e-", StringComparison.Ordinal);

    public bool TryGetDouble(out double value)
    {
        value = 0;
        return Kind == RecordValueKind.Number && double.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public bool FitsInt64 => Kind == RecordValueKind.Number && IsInteger
        && long.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    public bool FitsDecimal => Kind == RecordValueKind.Number
        && decimal.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
        && NormalizeNumber(value.ToString(CultureInfo.InvariantCulture)) == Scalar;
    public bool FitsDouble => TryGetDouble(out double value)
        && NormalizeNumber(value.ToString("R", CultureInfo.InvariantCulture)) == Scalar;

    /// <summary>Versioned, lossless identity used by hashing and JSON dictionary keys.</summary>
    public string CanonicalKey => _canonicalKey ??= "rv1:" + JsonSerializer.Serialize(this, IdentityOptions);

    public object? ToObject() => Kind switch
    {
        RecordValueKind.Null => null,
        RecordValueKind.Boolean => Scalar == "true",
        RecordValueKind.Number when long.TryParse(Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) => number,
        RecordValueKind.Number when decimal.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
            && Number(number.ToString(CultureInfo.InvariantCulture)).Equals(this) => number,
        RecordValueKind.Number => this,
        RecordValueKind.DateTime => DateTime.Parse(Scalar, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        RecordValueKind.DateTimeOffset => DateTimeOffset.Parse(Scalar, CultureInfo.InvariantCulture),
        RecordValueKind.Date => DateOnly.Parse(Scalar, CultureInfo.InvariantCulture),
        RecordValueKind.Time => TimeOnly.Parse(Scalar, CultureInfo.InvariantCulture),
        RecordValueKind.Duration => TimeSpan.Parse(Scalar, CultureInfo.InvariantCulture),
        RecordValueKind.Guid => Guid.Parse(Scalar),
        RecordValueKind.Binary => Convert.FromBase64String(Scalar),
        RecordValueKind.Array => Items.Select(v => v.ToObject()).ToList(),
        RecordValueKind.Object => Properties.ToDictionary(p => p.Key, p => p.Value.ToObject(), StringComparer.Ordinal),
        _ => Scalar
    };

    public string ToDisplayString() => Kind switch
    {
        RecordValueKind.Null => "null",
        RecordValueKind.Object or RecordValueKind.Array => ToJsonString(),
        _ => Scalar
    };

    public string ToJsonString(bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = indented
        })) WriteData(writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteData(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case RecordValueKind.Null: writer.WriteNullValue(); break;
            case RecordValueKind.Boolean: writer.WriteBooleanValue(Scalar == "true"); break;
            case RecordValueKind.Number when Scalar is not ("NaN" or "Infinity" or "-Infinity"): writer.WriteRawValue(Scalar); break;
            case RecordValueKind.Object:
                writer.WriteStartObject();
                foreach (var pair in Properties) { writer.WritePropertyName(pair.Key); pair.Value.WriteData(writer); }
                writer.WriteEndObject(); break;
            case RecordValueKind.Array:
                writer.WriteStartArray(); foreach (var item in Items) item.WriteData(writer); writer.WriteEndArray(); break;
            default: writer.WriteStringValue(Scalar); break;
        }
    }

    internal void WriteIdentity(Utf8JsonWriter writer)
    {
        writer.WriteStartArray(); writer.WriteStringValue(Kind.ToString());
        if (Kind == RecordValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var pair in Properties) { writer.WritePropertyName(pair.Key); pair.Value.WriteIdentity(writer); }
            writer.WriteEndObject();
        }
        else if (Kind == RecordValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in Items) item.WriteIdentity(writer); writer.WriteEndArray();
        }
        else writer.WriteStringValue(Scalar);
        writer.WriteEndArray();
    }

    internal static RecordValue ReadIdentity(JsonElement element, int depth = 0)
    {
        if (depth > 128) throw new JsonException("Record nesting exceeds 128 levels.");
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2
            || element[0].ValueKind != JsonValueKind.String
            || !Enum.TryParse(element[0].GetString(), out RecordValueKind kind)
            || Enum.GetName(kind) != element[0].GetString())
            throw new JsonException("Invalid typed record value.");
        var payload = element[1];
        if (kind == RecordValueKind.Object && payload.ValueKind == JsonValueKind.Object)
            return Object(payload.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, ReadIdentity(p.Value, depth + 1))));
        if (kind == RecordValueKind.Array && payload.ValueKind == JsonValueKind.Array)
            return new(kind, "", payload.EnumerateArray().Select(v => ReadIdentity(v, depth + 1)).ToImmutableArray());
        if (payload.ValueKind != JsonValueKind.String) throw new JsonException("Invalid scalar payload.");
        string scalar = payload.GetString()!;
        try
        {
            return kind switch
            {
                RecordValueKind.Null when scalar.Length == 0 => Null,
                RecordValueKind.Boolean when scalar is "true" or "false" => new(kind, scalar),
                RecordValueKind.Number => ReadNumber(scalar),
                RecordValueKind.String => FromString(scalar),
                RecordValueKind.DateTime => FromObject(DateTime.Parse(scalar, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                RecordValueKind.DateTimeOffset => FromObject(DateTimeOffset.Parse(scalar, CultureInfo.InvariantCulture)),
                RecordValueKind.Date => FromObject(DateOnly.Parse(scalar, CultureInfo.InvariantCulture)),
                RecordValueKind.Time => FromObject(TimeOnly.Parse(scalar, CultureInfo.InvariantCulture)),
                RecordValueKind.Duration => FromObject(TimeSpan.Parse(scalar, CultureInfo.InvariantCulture)),
                RecordValueKind.Guid => FromObject(Guid.Parse(scalar)),
                RecordValueKind.Binary => FromObject(Convert.FromBase64String(scalar)),
                _ => throw new JsonException("Invalid typed value payload.")
            };
        }
        catch (FormatException ex) { throw new JsonException("Invalid typed value payload.", ex); }
    }

    private static RecordValue ReadNumber(string scalar)
    {
        if (scalar is "NaN" or "Infinity" or "-Infinity") return Number(scalar);
        using var document = JsonDocument.Parse(scalar);
        if (document.RootElement.ValueKind != JsonValueKind.Number) throw new JsonException("Expected a numeric payload.");
        return Number(scalar);
    }

    public override string ToString() => ToDisplayString();
    public bool Equals(RecordValue? other) => other is not null && StringComparer.Ordinal.Equals(CanonicalKey, other.CanonicalKey);
    public override bool Equals(object? obj) => obj is RecordValue other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalKey);
    public int CompareTo(RecordValue? other) => other is null ? 1 : StringComparer.Ordinal.Compare(CanonicalKey, other.CanonicalKey);
    public static bool operator ==(RecordValue? left, RecordValue? right) => Equals(left, right);
    public static bool operator !=(RecordValue? left, RecordValue? right) => !Equals(left, right);
    public static bool operator <(RecordValue left, RecordValue right) => left.CompareTo(right) < 0;
    public static bool operator >(RecordValue left, RecordValue right) => left.CompareTo(right) > 0;
    public static bool operator <=(RecordValue left, RecordValue right) => left.CompareTo(right) <= 0;
    public static bool operator >=(RecordValue left, RecordValue right) => left.CompareTo(right) >= 0;
}

public sealed class RecordValueJsonConverter : JsonConverter<RecordValue>
{
    public override RecordValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return RecordValue.ReadIdentity(document.RootElement);
    }
    public override void Write(Utf8JsonWriter writer, RecordValue value, JsonSerializerOptions options) => value.WriteIdentity(writer);
    public override RecordValue ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string key = reader.GetString()!;
        if (!key.StartsWith("rv1:", StringComparison.Ordinal)) throw new JsonException("Legacy untyped value counts cannot be restored losslessly. Re-analyze the source with the current version.");
        using var document = JsonDocument.Parse(key[4..], new JsonDocumentOptions { MaxDepth = 512 });
        return RecordValue.ReadIdentity(document.RootElement);
    }
    public override void WriteAsPropertyName(Utf8JsonWriter writer, RecordValue value, JsonSerializerOptions options) => writer.WritePropertyName(value.CanonicalKey);
}
