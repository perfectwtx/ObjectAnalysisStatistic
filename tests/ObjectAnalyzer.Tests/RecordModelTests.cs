using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ObjectAnalyzer.Data;
using ObjectAnalyzer.Reporting;
using Engine = ObjectAnalyzer.Analysis.ObjectAnalyzer;

namespace ObjectAnalyzer.Tests;

public sealed class RecordModelTests
{
    private static RecordValue Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return RecordValue.FromJsonElement(doc.RootElement);
    }

    [Fact]
    public void ValueIdentity_DistinguishesTypesAndSentinelText()
    {
        object?[] values = [1, "1", null, "null", true, "true", new[] { 1 }, "[1]", "[Other values]"];
        var result = Engine.Analyze(values.Select(x => new Dictionary<string, object?> { ["x"] = x }));
        var field = Assert.Single(result.FieldStatistics);
        Assert.Equal(values.Length, result.TotalUniqueObjects);
        Assert.Equal(values.Length, field.DistinctCount);
        Assert.Equal(values.Length, field.ValueCounts.Count);
        Assert.Equal(1, field.ValueCounts[RecordValue.Null]);
        Assert.Equal(1, field.ValueCounts[RecordValue.FromString("null")]);
        Assert.Equal(1, field.ValueCounts[RecordValue.FromObject(1)]);
        Assert.Equal(0, field.OtherValueCount);
    }

    [Fact]
    public void NumericIdentity_IsExactAndIndependentOfClrRepresentation()
    {
        Assert.Equal(RecordValue.FromObject(1L), RecordValue.FromObject(1.00m));
        Assert.Equal(RecordValue.FromObject(1.0d), Json("1e0"));
        Assert.Equal(Json("0"), Json("-0.000e10000"));
        Assert.Equal(Json("1e1000"), Json("10e999"));
        Assert.NotEqual(Json("9007199254740992"), Json("9007199254740993"));
        Assert.NotEqual(Json("0.1234567890123456789012345678"), Json("0.1234567890123456789012345679"));
        Assert.Equal(0.1234567890123456789012345678m, Json("0.1234567890123456789012345678").ToObject());
        Assert.Equal(Json("1e1000"), RecordValue.FromObject(Json("1e1000").ToObject()));
    }

    [Fact]
    public void ValueIdentity_IsCultureIndependentAndObjectsAreOrderIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = RecordValue.FromObject(new Dictionary<string, object?> { ["b"] = 1.25m, ["a"] = (int[])[1, 2] });
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = Json("{\"a\":[1,2],\"b\":1.25}");
            Assert.Equal(french, english);
            Assert.Equal(french.GetHashCode(), english.GetHashCode());
            Assert.NotEqual(english, Json("{\"a\":[2,1],\"b\":1.25}"));
            Assert.NotEqual(Json("{\"id\":1}"), Json("{\"Id\":1}"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ValueIsAnImmutableSnapshotOfInput()
    {
        var items = new List<object?> { "original" };
        var source = new Dictionary<string, object?> { ["items"] = items };
        var value = RecordValue.FromObject(source);
        items[0] = "changed";
        source.Clear();
        Assert.Equal(Json("{\"items\":[\"original\"]}"), value);
    }

    [Fact]
    public void TypedValues_AndFrequencyKeys_RoundTripWithoutLosingTypes()
    {
        object?[] raw = [null, "null", 1, "1", 1.25m, DateTime.UnixEpoch, DateTimeOffset.UnixEpoch,
            new DateOnly(2026, 10, 17), new TimeOnly(12, 30), TimeSpan.FromMinutes(2), Guid.Empty,
            new byte[] { 1, 2 }, new Dictionary<string, object?> { ["a.b"] = "中文" }];
        var counts = raw.Select(RecordValue.FromObject).ToDictionary(x => x, _ => 1L);
        var restored = JsonSerializer.Deserialize<Dictionary<RecordValue, long>>(JsonSerializer.Serialize(counts))!;
        Assert.Equal(counts.Count, restored.Count);
        foreach (var value in counts.Keys)
        {
            Assert.Equal(1, restored[value]);
            Assert.Equal(value, RecordValue.FromObject(value.ToObject()));
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dictionary<RecordValue, long>>("{\"1\":1}"));
    }

    [Theory]
    [InlineData("[\"Number\",\"abc\"]")]
    [InlineData("[\"Number\",\"true\"]")]
    [InlineData("[\"Boolean\",\"yes\"]")]
    [InlineData("[\"Null\",\"null\"]")]
    [InlineData("[\"Binary\",\"not base64\"]")]
    [InlineData("[\"999\",\"value\"]")]
    public void TypedValueJson_RejectsInvalidPayloads(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RecordValue>(json));

    [Fact]
    public void CanonicalFieldAliases_HaveTheSameIdentity()
    {
        var result = Engine.Analyze([
            new Dictionary<string, object?> { ["a.b"] = 1, ["x"] = 2 },
            new Dictionary<string, object?> { ["x"] = 2, ["a[\"b\"]"] = 1 }
        ]);
        Assert.Equal(1, result.TotalUniqueObjects);
        Assert.Equal(2, result.FieldStatistics.Count);
        Assert.Equal(2, result.FieldStatistics.Single(x => x.FieldName == "a.b").Count);
    }

    [Fact]
    public void NestedObjectDedup_UsesCanonicalValues()
    {
        var records = new[] { Json("{\"x\":{\"a\":1,\"b\":[true,null]}}"), Json("{\"x\":{\"b\":[true,null],\"a\":1.0}}") }
            .Select(x => RecordNormalizer.Normalize(x, flatten: false));
        var result = Engine.AnalyzeRecords(records);
        Assert.Equal(1, result.TotalUniqueObjects);
        Assert.Equal(1, result.TotalDuplicateGroups);
    }

    [Fact]
    public void OverflowDoesNotCollideWithRealData_AndHllIncludesNull()
    {
        var result = Engine.Analyze(new object?[] { "[Other values]", "other", null, "null", 1, "1" }
            .Select(x => new Dictionary<string, object?> { ["x"] = x }),
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = 1 }, maxTrackedValues: 5);
        var field = Assert.Single(result.FieldStatistics);
        Assert.Equal(1, field.ValueCounts["[Other values]"]);
        Assert.Equal(1, field.OtherValueCount);
        Assert.True(field.DistinctApproximate);
        Assert.InRange(field.DistinctCount!.Value, 5, 7);
        Assert.Equal(field.Count, field.ValueCounts.Values.Sum() + field.OtherValueCount);
    }

    [Fact]
    public void EnumRules_MatchTypesAndExactNumbers()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { EnumValues = [JsonSerializer.SerializeToElement(1)] } } };
        var result = Engine.Analyze(new object?[] { 1L, 1.0m, "1", true, null }
            .Select(x => new Dictionary<string, object?> { ["x"] = x }), rules);
        Assert.Equal(3, Assert.Single(result.FieldStatistics).EnumViolations.Count);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a[0]")]
    [InlineData("")]
    [InlineData("a\"b\\c")]
    [InlineData("a,b")]
    [InlineData("中文\n字段")]
    public void FieldPath_RoundTripsLiteralNames(string name)
    {
        var path = FieldPath.Property(name).AppendIndex(2).AppendProperty("leaf");
        Assert.Equal(path, FieldPath.Parse(path.ToString()));
        Assert.Equal(name, FieldPath.Parse(path.ToString()).Segments[0].Name);
        Assert.Equal(path, JsonSerializer.Deserialize<FieldPath>(JsonSerializer.Serialize(path)));
    }

    [Theory]
    [InlineData("a[")]
    [InlineData("a[-1]")]
    [InlineData("a..b")]
    [InlineData("a.")]
    [InlineData("a[0]b")]
    public void FieldPath_RejectsMalformedPaths(string text) => Assert.Throws<FormatException>(() => FieldPath.Parse(text));

    [Fact]
    public void Paths_DistinguishLiteralNamesIndicesCaseAndRoot()
    {
        Assert.NotEqual(FieldPath.Property("a.b"), FieldPath.Parse("a.b"));
        Assert.NotEqual(FieldPath.Property("a[0]"), FieldPath.Parse("a[0]"));
        Assert.NotEqual(FieldPath.Property("id"), FieldPath.Property("Id"));
        Assert.NotEqual(FieldPath.Property(""), FieldPath.Root);
        Assert.False(FieldPath.Property("a").IsPrefixOf(FieldPath.Property("a.b")));
        Assert.Equal("user.name", FieldPath.Property("user").AppendProperty("name").ToString());
        var list = FieldPath.ParseList("[\"a,b\"],a.b, [\"a\\\"b\"]");
        Assert.Equal(3, list.Count);
        Assert.Equal(FieldPath.Property("a,b"), list[0]);
        Assert.Equal(FieldPath.Property("a\"b"), list[2]);
    }

    [Fact]
    public void Normalization_PreservesCollidingNamesEmptyContainersAndNestedArrays()
    {
        var normalized = RecordNormalizer.Normalize(Json("""{"a.b":1,"a":{"b":2},"x[0]":3,"x":[4],"":5,"id":6,"Id":7,"matrix":[[8]],"empty":[],"obj":{}}"""));
        Assert.Equal(10, normalized.Count);
        Assert.Equal(RecordValue.FromObject(1), normalized[FieldPath.Property("a.b")]);
        Assert.Equal(RecordValue.FromObject(2), normalized[FieldPath.Parse("a.b")]);
        Assert.Equal(RecordValue.FromObject(8), normalized[FieldPath.Parse("matrix[0][0]")]);
        Assert.Equal(RecordValueKind.Array, normalized[FieldPath.Property("empty")].Kind);
        Assert.Equal(RecordValueKind.Object, normalized[FieldPath.Property("obj")].Kind);
        Assert.Empty(RecordNormalizer.Normalize(Json("{}")));
    }

    [Fact]
    public void JsonAndJsonlParsers_UseSamePathsAndPreciseValues()
    {
        const string json = """{"a.b":0.1234567890123456789012345678,"a":{"b":2},"arr":[[3]],"":null}""";
        foreach (string format in new[] { "json", "jsonl" })
        {
            string path = Path.Combine(Path.GetTempPath(), $"oa-model-{Guid.NewGuid():N}.{format}");
            try
            {
                File.WriteAllText(path, json);
                var row = Assert.Single(FileParsers.ParseByFormat(path, format, new ObjectAnalyzer.Parsing.ParseOptions { Flatten = true }));
                Assert.Equal(4, row.Count);
                Assert.Equal(0.1234567890123456789012345678m, row["[\"a.b\"]"]);
                Assert.Equal(2L, row["a.b"]);
                Assert.Equal(3L, row["arr[0][0]"]);
                Assert.Null(row["[\"\"]"]);
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void SelectionAndFilter_ResolveQuotedFieldPaths()
    {
        var row = new Dictionary<string, object?> { ["[\"a.b\"]"] = 1, ["a.b"] = 2, ["a[0].x"] = 3 };
        var processor = new FieldTransformProcessor(null, ["[\"a.b\"]"], "[\"a.b\"] == 1");
        var selected = Assert.Single(processor.Wrap([row]));
        Assert.Equal("[\"a.b\"]", Assert.Single(selected).Key);
        Assert.True(new FieldFilter("a[0].x == 3").Evaluate(row));
        Assert.False(new FieldFilter("[\"a.b\"] == 2").Evaluate(row));
        Assert.False(new FieldFilter("A.b == 2").Evaluate(row));
    }

    [Fact]
    public void Schema_PreservesNamesAndUsesNumericEvidence()
    {
        var result = Engine.AnalyzeRecords(new[] { Json("""{"a.b":1,"a":{"b":2},"id":1.5,"Id":2.5,"a-b":3,"a_b":4}""") }
            .Select(x => RecordNormalizer.Normalize(x)));
        using var doc = JsonDocument.Parse(SchemaExporter.GenerateJsonSchema(result));
        var properties = doc.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("a.b", out _));
        Assert.True(properties.GetProperty("a").GetProperty("properties").TryGetProperty("b", out _));
        Assert.True(properties.TryGetProperty("id", out _));
        Assert.True(properties.TryGetProperty("Id", out _));
        decimal[] fractions = [1.5m, 2.5m];
        var fractional = Engine.Analyze(fractions.Select(x => new Dictionary<string, object?> { ["x"] = x }));
        Assert.Contains("public double X", SchemaExporter.GenerateCSharp(fractional));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GeneratedModel", [CSharpSyntaxTree.ParseText(SchemaExporter.GenerateCSharp(result))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Schema_PreservesNestedArraysAndPreciseNumericTypes()
    {
        var row = RecordNormalizer.Normalize(Json("""{"matrix":[[1]],"precise":0.1234567890123456789012345678,"huge":1e1000}"""));
        var result = Engine.AnalyzeRecords([row]);
        string csharp = SchemaExporter.GenerateCSharp(result);
        Assert.Contains("public long[][] Matrix", csharp);
        Assert.Contains("public decimal Precise", csharp);
        Assert.Contains("public System.Text.Json.JsonElement Huge", csharp);
        Assert.Contains("matrix: number[][]", SchemaExporter.GenerateTypeScript(result));
        using var schema = JsonDocument.Parse(SchemaExporter.GenerateJsonSchema(result));
        var item = schema.RootElement.GetProperty("properties").GetProperty("matrix").GetProperty("items");
        Assert.Equal("array", item.GetProperty("type").GetString());
        Assert.Equal("integer", item.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void LegacySketches_AreNotComparedToTypedValueSketches()
    {
        Assert.Throws<InvalidDataException>(() => DistributionDiffer.Compute(
            new DistributionSnapshot { SnapshotVersion = "1.0" }, new DistributionSnapshot()));
        Assert.Throws<InvalidDataException>(() => SchemaDiffer.Compute(
            new SchemaSnapshot { SnapshotVersion = "1.0" }, new SchemaSnapshot()));
    }

    [Fact]
    public void Lineage_UsesStructuralParentage()
    {
        var lineage = DataLineageBuilder.Build([
            new ObjectRelationship { Path = "[\"a.b\"]", Kind = "Object", TargetFields = ["x.y"] },
            new ObjectRelationship { Path = "a", Kind = "Object", TargetFields = ["b"] }
        ]);
        Assert.Contains(lineage.Nodes, node => node.Path == "[\"a.b\"][\"x.y\"]");
        Assert.Contains(lineage.Nodes, node => node.Path == "a.b");
    }

    [Fact]
    public void Preflight_UsesStructuralPathsIncludingArrayParents()
    {
        var result = PreflightChecker.Build(["items", "[\"a.b\"]"], ["items[0].id", "a.b.child"]);
        Assert.Equal("items", Assert.Single(result.MatchedRuleFields));
        Assert.Equal("[\"a.b\"]", Assert.Single(result.UnmatchedRuleFields));
        Assert.Equal(FieldPath.Property("a,b"), Assert.Single(FieldPath.ParseList(FieldPath.Property("a,b").ToString())));
    }
}
