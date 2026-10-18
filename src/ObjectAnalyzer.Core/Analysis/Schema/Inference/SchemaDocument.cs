using System.Collections.Immutable;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Analysis;

public enum SchemaPresence { Required, Optional, Unknown }
public sealed record SchemaDiagnostic(string Code, string Path, string Message);
public sealed record SchemaNumericDomain(bool Integral, bool FitsInt64, bool FitsDecimal, bool FitsDouble,
    string? Minimum, string? Maximum, bool RangeExact, int? IntegerDigits, int? Scale);
public sealed record SchemaProperty(string Name, SchemaType Type, SchemaPresence Presence);

/// <summary>Immutable evidence, independent of every target language. Unknown is not a synonym for string.</summary>
public sealed record SchemaType
{
    public ImmutableArray<RecordValueKind> Kinds { get; init; } = [];
    public bool HasUnknownValues { get; init; }
    public bool ShapeIncomplete { get; init; }
    public bool EmptyArrayObserved { get; init; }
    public ImmutableArray<SchemaProperty> Properties { get; init; } = [];
    public SchemaType? Element { get; init; }
    public SchemaNumericDomain? Numeric { get; init; }
    public ImmutableArray<string> SemanticTypes { get; init; } = [];
    public ImmutableArray<string> VerifiedSemanticTypes { get; init; } = [];
    public ImmutableArray<RecordValue> EnumValues { get; init; } = [];
    public bool AllowsNull => Kinds.Contains(RecordValueKind.Null);
}
public sealed record SchemaDocument(SchemaType Root, ImmutableArray<SchemaDiagnostic> Diagnostics)
{
    public string Version { get; } = "schema-model/v1";
}
public sealed record SchemaInferenceOptions
{
    public int MaxNodes { get; init; } = 20_000;
    public int MaxDepth { get; init; } = 128;
}
