using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Analysis;

/// <summary>Inference is a commutative evidence merge; property order and array indices cannot overwrite types.</summary>
public static class SchemaInference
{
    public static SchemaDocument Infer(AnalysisResult result, SchemaInferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new Builder(options ?? new()).Build(result);
    }

    private sealed class Entry
    {
        public FieldStatistic? Field;
        public SortedDictionary<string, Entry> Properties { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<int, Entry> Indices { get; } = new();
    }
    private sealed class Builder(SchemaInferenceOptions options)
    {
        private int _nodes;
        private readonly List<SchemaDiagnostic> _diagnostics = new();
        private void Admit(int depth)
        {
            if (depth > options.MaxDepth) throw new AnalysisResourceLimitException("schema-depth", options.MaxDepth);
            if (++_nodes > options.MaxNodes) throw new AnalysisResourceLimitException("schema-nodes", options.MaxNodes);
        }
        private void Note(string code, string path, string message) => _diagnostics.Add(new(code, path, message));
        public SchemaDocument Build(AnalysisResult result)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxNodes, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDepth, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(result.TotalObjects);
            var root = new Entry(); Admit(0);
            foreach (var field in result.FieldStatistics.OrderBy(field => field.FieldName, StringComparer.Ordinal))
            {
                var current = root; int depth = 0;
                foreach (var segment in FieldPath.Parse(field.FieldName).Segments)
                {
                    depth++;
                    if (segment.IsIndex)
                    {
                        if (!current.Indices.TryGetValue(segment.Index!.Value, out var child))
                        { Admit(depth); child = new(); current.Indices.Add(segment.Index.Value, child); }
                        current = child;
                    }
                    else
                    {
                        if (!current.Properties.TryGetValue(segment.Name!, out var child))
                        { Admit(depth); child = new(); current.Properties.Add(segment.Name!, child); }
                        current = child;
                    }
                }
                if (current.Field is not null) throw new InvalidDataException("Duplicate schema evidence path.");
                current.Field = field;
            }
            if (root.Field is not null || root.Indices.Count > 0) throw new InvalidDataException("Schema root must be a record; use a quoted property segment for an empty field name.");
            var document = BuildEntry(root, result.TotalObjects, "", 0);
            // An input is a record even when it contains no fields.
            document = document with { Kinds = document.Kinds.Append(RecordValueKind.Object).Distinct().Order().ToImmutableArray() };
            return new(document, _diagnostics.Distinct().OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal).ToImmutableArray());
        }

        private static (long Low, long High) Bounds(Entry entry, long maximum)
        {
            long direct = Math.Clamp(entry.Field?.Count ?? 0, 0, maximum);
            var children = entry.Properties.Values.Concat(entry.Indices.Values).Select(child => Bounds(child, maximum)).ToArray();
            if (children.Length == 0) return (direct, direct);
            long low = Math.Max(direct, children.Max(pair => pair.Low));
            long high = direct;
            foreach (var child in children) high = Math.Min(maximum, high > maximum - child.High ? maximum : high + child.High);
            return (low, high);
        }
        private SchemaType BuildEntry(Entry entry, long maximum, string path, int depth)
        {
            var direct = entry.Field is null ? new SchemaType() : FromField(entry.Field, path, depth);
            if (entry.Properties.Count > 0)
            {
                var objectBounds = path.Length == 0 ? (Low: maximum, High: maximum) : Bounds(new EntryWithChildren(entry).Value, maximum);
                long population = objectBounds.High;
                var properties = entry.Properties.Select(pair =>
                {
                    var bounds = Bounds(pair.Value, maximum);
                    var presence = population > 0 && bounds.Low >= population ? SchemaPresence.Required
                        : bounds.High < objectBounds.Low ? SchemaPresence.Optional : SchemaPresence.Unknown;
                    string childPath = FieldPath.Parse(path).AppendProperty(pair.Key).ToString();
                    if (presence == SchemaPresence.Unknown) Note("presence-unknown", childPath, "Aggregate counts cannot prove joint parent/child presence; exported as optional.");
                    return new SchemaProperty(pair.Key, BuildEntry(pair.Value, maximum, childPath, depth + 1), presence);
                }).ToImmutableArray();
                direct = Merge(direct, new() { Kinds = [RecordValueKind.Object], Properties = properties });
            }
            if (entry.Indices.Count > 0)
            {
                SchemaType? element = null;
                foreach (var pair in entry.Indices)
                {
                    var candidate = BuildEntry(pair.Value, maximum, FieldPath.Parse(path).AppendIndex(pair.Key).ToString(), depth + 1);
                    element = element is null ? candidate : Merge(element, candidate);
                }
                direct = Merge(direct, new() { Kinds = [RecordValueKind.Array], Element = element });
            }
            return direct;
        }
        // Structural children describe an object population; a direct null/scalar is not an object occurrence.
        private sealed class EntryWithChildren
        {
            public Entry Value { get; } = new();
            public EntryWithChildren(Entry source) { foreach (var pair in source.Properties) Value.Properties.Add(pair.Key, pair.Value); }
        }

        private SchemaType FromField(FieldStatistic field, string path, int depth)
        {
            var kinds = field.TypeCounts.Where(pair => pair.Value > 0)
                .Select(pair => Enum.TryParse<RecordValueKind>(pair.Key, out var kind) ? (RecordValueKind?)kind : null)
                .OfType<RecordValueKind>().Distinct().Order().ToImmutableArray();
            if (kinds.Length == 0) kinds = field.ValueCounts.Keys.Select(value => value.Kind).Distinct().Order().ToImmutableArray();
            if (field.NullAppeared && !kinds.Contains(RecordValueKind.Null)) kinds = kinds.Append(RecordValueKind.Null).Order().ToImmutableArray();
            bool redacted = field.ProjectedValues?.Any(value => value.Redacted) == true;
            bool incomplete = field.ValueCountsTruncated || redacted;
            var values = new ValueEvidence(this);
            if (!redacted)
                foreach (var pair in field.ValueCounts.OrderBy(pair => pair.Key)) values.Observe(pair.Key, pair.Value, depth);
            var shape = values.Complete();
            if (incomplete && kinds.Any(kind => kind is RecordValueKind.Object or RecordValueKind.Array))
            {
                Note("shape-incomplete", path, "Nested evidence was truncated or redacted; unknown properties and element variants remain possible.");
                shape = Open(shape);
            }
            if (redacted) Note("redacted-evidence", path, "Masked examples are not used to infer source types or numeric ranges.");
            var numeric = kinds.Contains(RecordValueKind.Number) ? shape.Numeric ?? new(true, true, true, true, null, null, false, null, null) : null;
            if (numeric is not null)
                numeric = numeric with
                {
                    Integral = numeric.Integral && field.NumbersAreIntegral, FitsInt64 = numeric.FitsInt64 && field.NumbersFitInt64,
                    FitsDecimal = numeric.FitsDecimal && field.NumbersFitDecimal, FitsDouble = numeric.FitsDouble && field.NumbersFitDouble,
                    RangeExact = !incomplete && numeric.Minimum is not null,
                    IntegerDigits = incomplete ? null : numeric.IntegerDigits, Scale = incomplete ? null : numeric.Scale
                };
            var verified = !incomplete && field.SemanticType is { } semantic && VerifySemantic(field, semantic)
                ? ImmutableArray.Create(semantic) : ImmutableArray<string>.Empty;
            if (verified.Length == 0 && field.SemanticType is "DateTime" or "UUID" or "Email" or "IPv4" or "URL")
                Note("semantic-hint-unverified", path, "The semantic label is not proven for every retained and omitted string; it is not used to narrow the source type or format.");
            return shape with
            {
                Kinds = kinds, HasUnknownValues = kinds.Length == 0, ShapeIncomplete = incomplete && kinds.Any(kind => kind is RecordValueKind.Object or RecordValueKind.Array),
                Numeric = numeric, SemanticTypes = string.IsNullOrEmpty(field.SemanticType) ? [] : [field.SemanticType],
                VerifiedSemanticTypes = verified,
                EnumValues = (field.AppliedRule?.EnumValues ?? []).Select(RecordValue.FromJsonElement).Distinct().Order().ToImmutableArray()
            };
        }

        private sealed class ValueEvidence(Builder owner)
        {
            private readonly HashSet<RecordValueKind> _kinds = new();
            private readonly SortedDictionary<string, ValueEvidence> _properties = new(StringComparer.Ordinal);
            private ValueEvidence? _element;
            private long _seen, _objects;
            private bool _emptyArray;
            private SchemaNumericDomain? _numeric;
            public void Observe(RecordValue value, long weight, int depth)
            {
                if (depth > optionsDepth) throw new AnalysisResourceLimitException("schema-depth", optionsDepth);
                _seen = checked(_seen + weight); _kinds.Add(value.Kind);
                if (value.Kind == RecordValueKind.Object)
                {
                    _objects = checked(_objects + weight);
                    foreach (var pair in value.Properties)
                    {
                        if (!_properties.TryGetValue(pair.Key, out var child)) { owner.Admit(depth + 1); child = new(owner); _properties.Add(pair.Key, child); }
                        child.Observe(pair.Value, weight, depth + 1);
                    }
                }
                else if (value.Kind == RecordValueKind.Array)
                {
                    _emptyArray |= value.Items.Length == 0;
                    foreach (var item in value.Items)
                    {
                        if (_element is null) { owner.Admit(depth + 1); _element = new(owner); }
                        _element.Observe(item, weight, depth + 1);
                    }
                }
                else if (value.Kind == RecordValueKind.Number)
                    _numeric = MergeNumbers(_numeric, value.Scalar is "NaN" or "Infinity" or "-Infinity"
                        ? new(false, false, false, false, null, null, false, null, null) : Number(value));
            }
            private int optionsDepth => owner.optionsDepth;
            public SchemaType Complete() => new()
            {
                Kinds = _kinds.Order().ToImmutableArray(), Numeric = _numeric, EmptyArrayObserved = _emptyArray,
                Element = _element?.Complete(), Properties = _properties.Select(pair => new SchemaProperty(pair.Key, pair.Value.Complete(),
                    pair.Value._seen == _objects ? SchemaPresence.Required : SchemaPresence.Optional)).ToImmutableArray()
            };
        }
        private int optionsDepth => options.MaxDepth;
    }

    private static SchemaType Open(SchemaType type) => type with
    {
        ShapeIncomplete = true,
        Properties = type.Properties.Select(property => property with { Presence = SchemaPresence.Unknown,
            Type = Open(property.Type) with { HasUnknownValues = true } }).ToImmutableArray(),
        Element = type.Element is null ? null : Open(type.Element) with { HasUnknownValues = true }
    };

    internal static SchemaType Merge(SchemaType left, SchemaType right)
    {
        if (left.Kinds.Length == 0 && !left.HasUnknownValues && left.Properties.Length == 0 && left.Element is null) return right;
        if (right.Kinds.Length == 0 && !right.HasUnknownValues && right.Properties.Length == 0 && right.Element is null) return left;
        var properties = new List<SchemaProperty>();
        foreach (string name in left.Properties.Select(item => item.Name).Concat(right.Properties.Select(item => item.Name)).Distinct().Order(StringComparer.Ordinal))
        {
            var a = left.Properties.FirstOrDefault(item => item.Name == name); var b = right.Properties.FirstOrDefault(item => item.Name == name);
            if (a is not null && b is not null)
                properties.Add(new(name, Merge(a.Type, b.Type), a.Presence == b.Presence ? a.Presence
                    : a.Presence == SchemaPresence.Optional || b.Presence == SchemaPresence.Optional ? SchemaPresence.Optional : SchemaPresence.Unknown));
            else
            {
                var property = a ?? b!; var other = a is null ? left : right;
                properties.Add(property with { Presence = other.Kinds.Contains(RecordValueKind.Object)
                    ? other.ShapeIncomplete ? SchemaPresence.Unknown : SchemaPresence.Optional
                    : other.HasUnknownValues ? SchemaPresence.Unknown : property.Presence });
            }
        }
        SchemaType? element = left.Element is null ? right.Element : right.Element is null ? left.Element : Merge(left.Element, right.Element);
        if (element is not null && (left.ShapeIncomplete || right.ShapeIncomplete)) element = element with { HasUnknownValues = true };
        return new()
        {
            Kinds = left.Kinds.Concat(right.Kinds).Distinct().Order().ToImmutableArray(),
            HasUnknownValues = left.HasUnknownValues || right.HasUnknownValues, ShapeIncomplete = left.ShapeIncomplete || right.ShapeIncomplete,
            EmptyArrayObserved = left.EmptyArrayObserved || right.EmptyArrayObserved, Properties = properties.ToImmutableArray(), Element = element,
            Numeric = MergeNumbers(left.Numeric, right.Numeric), SemanticTypes = left.SemanticTypes.Concat(right.SemanticTypes).Distinct().Order(StringComparer.Ordinal).ToImmutableArray(),
            VerifiedSemanticTypes = !left.Kinds.Contains(RecordValueKind.String) ? right.VerifiedSemanticTypes
                : !right.Kinds.Contains(RecordValueKind.String) ? left.VerifiedSemanticTypes
                : left.VerifiedSemanticTypes.Length > 0 && right.VerifiedSemanticTypes.Length > 0
                    ? left.VerifiedSemanticTypes.Concat(right.VerifiedSemanticTypes).Distinct().Order(StringComparer.Ordinal).ToImmutableArray() : [],
            EnumValues = left.EnumValues.Length > 0 && right.EnumValues.Length > 0 ? left.EnumValues.Concat(right.EnumValues).Distinct().Order().ToImmutableArray() : []
        };
    }

    private static bool VerifySemantic(FieldStatistic field, string semantic)
    {
        var strings = field.ValueCounts.Keys.Where(value => value.Kind == RecordValueKind.String).ToArray();
        if (strings.Length == 0) return false;
        return strings.All(value => semantic switch
        {
            "UUID" => Guid.TryParseExact(value.Scalar, "D", out _),
            "DateTime" => System.Text.RegularExpressions.Regex.IsMatch(value.Scalar,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                && DateTimeOffset.TryParse(value.Scalar, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "Email" => value.Scalar.All(char.IsAscii) && System.Net.Mail.MailAddress.TryCreate(value.Scalar, out var email)
                && email.Address == value.Scalar && value.Scalar.Contains('.', StringComparison.Ordinal) && !value.Scalar.Any(char.IsWhiteSpace),
            "IPv4" => System.Net.IPAddress.TryParse(value.Scalar, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.ToString() == value.Scalar,
            "URL" => Uri.IsWellFormedUriString(value.Scalar, UriKind.Absolute),
            _ => false
        });
    }

    private static SchemaNumericDomain Number(RecordValue value)
    {
        string text = value.Scalar;
        var (digits, exponent, _) = Parts(text);
        BigInteger integerDigits = BigInteger.Max(0, digits.Length + exponent), scale = BigInteger.Max(0, -exponent);
        return new(value.IsInteger, value.FitsInt64, value.FitsDecimal, value.FitsDouble, text, text, true,
            integerDigits <= int.MaxValue ? (int)integerDigits : null, scale <= int.MaxValue ? (int)scale : null);
    }
    private static (string Digits, BigInteger Exponent, int Sign) Parts(string text)
    {
        int sign = text.StartsWith('-') ? -1 : 1; string unsigned = text.TrimStart('-');
        int e = unsigned.IndexOf('e'); string digits = e < 0 ? unsigned : unsigned[..e];
        BigInteger exponent = e < 0 ? 0 : BigInteger.Parse(unsigned[(e + 1)..], CultureInfo.InvariantCulture);
        int point = digits.IndexOf('.');
        if (point >= 0) { exponent -= digits.Length - point - 1; digits = digits.Remove(point, 1); }
        digits = digits.TrimStart('0'); if (digits.Length == 0) digits = "0";
        return (digits, exponent, digits == "0" ? 0 : sign);
    }
    private static int CompareNumbers(string left, string right)
    {
        var a = Parts(left); var b = Parts(right);
        if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
        if (a.Sign == 0) return 0;
        int magnitude = (a.Exponent + a.Digits.Length).CompareTo(b.Exponent + b.Digits.Length);
        if (magnitude != 0) return magnitude * a.Sign;
        int width = Math.Max(a.Digits.Length, b.Digits.Length);
        return string.CompareOrdinal(a.Digits.PadRight(width, '0'), b.Digits.PadRight(width, '0')) * a.Sign;
    }
    private static SchemaNumericDomain? MergeNumbers(SchemaNumericDomain? left, SchemaNumericDomain? right)
    {
        if (left is null) return right; if (right is null) return left;
        return new(left.Integral && right.Integral, left.FitsInt64 && right.FitsInt64, left.FitsDecimal && right.FitsDecimal, left.FitsDouble && right.FitsDouble,
            left.Minimum is null ? right.Minimum : right.Minimum is null || CompareNumbers(left.Minimum, right.Minimum) <= 0 ? left.Minimum : right.Minimum,
            left.Maximum is null ? right.Maximum : right.Maximum is null || CompareNumbers(left.Maximum, right.Maximum) >= 0 ? left.Maximum : right.Maximum,
            left.RangeExact && right.RangeExact,
            left.IntegerDigits.HasValue && right.IntegerDigits.HasValue ? Math.Max(left.IntegerDigits.Value, right.IntegerDigits.Value) : null,
            left.Scale.HasValue && right.Scale.HasValue ? Math.Max(left.Scale.Value, right.Scale.Value) : null);
    }
}
