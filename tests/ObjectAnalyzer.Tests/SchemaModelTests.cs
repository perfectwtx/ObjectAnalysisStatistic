using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using ObjectAnalyzer.Data;
using YamlDotNet.RepresentationModel;

namespace ObjectAnalyzer.Tests;

public sealed class SchemaModelTests
{
    private static AnalysisResult Result(params string[] records) => AnalyzeRecords(records.Select(text =>
    { using var document = JsonDocument.Parse(text); return RecordNormalizer.Normalize(RecordValue.FromJsonElement(document.RootElement)); }));
    private static SchemaProperty Property(SchemaType type, string name) => Assert.Single(type.Properties, item => item.Name == name);
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void MissingAndNull_AreIndependentAndRequiredNullIsNotOptional()
    {
        var graph = SchemaInference.Infer(Result("{\"requiredNull\":null,\"optional\":1}", "{\"requiredNull\":null}"));
        var nullable = Property(graph.Root, "requiredNull"); var optional = Property(graph.Root, "optional");
        Assert.Equal(SchemaPresence.Required, nullable.Presence); Assert.True(nullable.Type.AllowsNull);
        Assert.Equal(SchemaPresence.Optional, optional.Presence); Assert.False(optional.Type.AllowsNull);
        string typescript = SchemaExporter.Generate(graph, "typescript").Content;
        Assert.Contains("requiredNull: null;", typescript); Assert.Contains("optional?: number;", typescript);
        using var schema = JsonDocument.Parse(SchemaExporter.Generate(graph, "json-schema").Content);
        Assert.Contains(schema.RootElement.GetProperty("required").EnumerateArray(), item => item.GetString() == "requiredNull");
    }

    [Fact]
    public void HeterogeneousAndNestedArrays_MergeEveryIndexInsteadOfOverwriting()
    {
        var graph = SchemaInference.Infer(Result("{\"items\":[1,\"x\",null,{\"a\":true}],\"matrix\":[[1],[\"x\"]]}"));
        var element = Property(graph.Root, "items").Type.Element!;
        Assert.Equal(new[] { RecordValueKind.Null, RecordValueKind.Number, RecordValueKind.String, RecordValueKind.Object }, element.Kinds.ToArray());
        Assert.Contains(RecordValueKind.Number, Property(graph.Root, "matrix").Type.Element!.Element!.Kinds);
        Assert.Contains(RecordValueKind.String, Property(graph.Root, "matrix").Type.Element!.Element!.Kinds);
        using var schema = JsonDocument.Parse(SchemaExporter.Generate(graph, "json-schema").Content);
        Assert.Equal(4, schema.RootElement.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("anyOf").GetArrayLength());
    }

    [Fact]
    public void ArrayObjectProperties_AreOptionalWhenAbsentFromAnotherElementShape()
    {
        var graph = SchemaInference.Infer(Result("{\"items\":[{\"a\":1},{\"b\":true}]}"));
        var element = Property(graph.Root, "items").Type.Element!;
        Assert.Equal(SchemaPresence.Optional, Property(element, "a").Presence);
        Assert.Equal(SchemaPresence.Optional, Property(element, "b").Presence);
    }

    [Fact]
    public void EmptyObjectsArraysAndUnknownElements_AreDistinct()
    {
        var graph = SchemaInference.Infer(Result("{\"object\":{},\"array\":[]}"));
        var array = Property(graph.Root, "array").Type; var obj = Property(graph.Root, "object").Type;
        Assert.Equal(new[] { RecordValueKind.Object }, obj.Kinds.ToArray()); Assert.Empty(obj.Properties);
        Assert.Equal(new[] { RecordValueKind.Array }, array.Kinds.ToArray()); Assert.True(array.EmptyArrayObserved); Assert.Null(array.Element);
        Assert.Contains("array: unknown[];", SchemaExporter.Generate(graph, "typescript").Content);
        Assert.Contains(SchemaExporter.Generate(graph, "json-schema").Diagnostics, item => item.Code == "array-element-unknown");
    }

    [Fact]
    public void RecordPropertyAndStatisticOrder_DoNotChangeInferenceOrAnyBackend()
    {
        var first = Result("{\"b\":true,\"items\":[1,\"x\"],\"a\":{\"y\":1,\"x\":null}}", "{\"a\":{\"x\":\"s\"},\"items\":[false]}");
        var second = Result("{\"items\":[false],\"a\":{\"x\":\"s\"}}", "{\"a\":{\"x\":null,\"y\":1},\"items\":[1,\"x\"],\"b\":true}");
        second.FieldStatistics.Reverse();
        var a = SchemaInference.Infer(first); var b = SchemaInference.Infer(second);
        Assert.Equal(Json(a), Json(b));
        foreach (string target in new[] { "csharp", "typescript", "json-schema", "openapi", "openapi-yaml", "proto", "sql", "sqlite", "mysql", "sqlserver" })
            Assert.Equal(SchemaExporter.Generate(a, target).Content, SchemaExporter.Generate(b, target).Content);
    }

    [Fact]
    public void ExactNumericDomain_IsSeparateFromMeansAndKeepsHugeExponentRanges()
    {
        var graph = SchemaInference.Infer(Result("{\"x\":1.5,\"big\":1e1000}", "{\"x\":2.5,\"big\":-1e999}"));
        var number = Property(graph.Root, "x").Type.Numeric!;
        Assert.False(number.Integral); Assert.Equal("1.5", number.Minimum); Assert.Equal("2.5", number.Maximum);
        var huge = Property(graph.Root, "big").Type.Numeric!;
        Assert.Equal("-1e999", huge.Minimum); Assert.Equal("1e1000", huge.Maximum); Assert.False(huge.FitsDouble);
        Assert.Contains("\"x\" REAL NOT NULL", SchemaExporter.Generate(graph, "sqlite").Content);
        Assert.Contains("\"big\" TEXT NOT NULL", SchemaExporter.Generate(graph, "sqlite").Content);
        Assert.Contains(SchemaExporter.Generate(graph, "csharp").Diagnostics, item => item.Code == "csharp-number-precision");
    }

    [Fact]
    public void TruncatedNestedEvidence_DoesNotInventClosedShapesOrStringElements()
    {
        using var json = JsonDocument.Parse("{\"items\":[{\"value\":1}]}");
        var state = new AnalysisAccumulator(maxTrackedValues: 0);
        state.Observe(RecordNormalizer.Normalize(RecordValue.FromJsonElement(json.RootElement), flatten: false));
        var graph = SchemaInference.Infer(state.Complete());
        var array = Property(graph.Root, "items").Type;
        Assert.True(array.ShapeIncomplete); Assert.Null(array.Element);
        Assert.Contains(graph.Diagnostics, item => item.Code == "shape-incomplete");
        Assert.Contains("unknown[]", SchemaExporter.Generate(graph, "typescript").Content);
    }

    [Fact]
    public void EmptyArrayEvidence_CannotEraseElementTypesFromNonEmptyIndices()
    {
        var graph = SchemaInference.Infer(Result("{\"items\":[]}", "{\"items\":[1,2]}"));
        var array = Property(graph.Root, "items").Type;
        Assert.True(array.EmptyArrayObserved); Assert.Equal(new[] { RecordValueKind.Number }, array.Element!.Kinds.ToArray());
    }

    [Fact]
    public void ParentScalarAndDescendantObjectEvidence_FormAUnion()
    {
        var graph = SchemaInference.Infer(Result("{\"value\":1}", "{\"value\":{\"child\":true}}"));
        Assert.Equal(new[] { RecordValueKind.Number, RecordValueKind.Object }, Property(graph.Root, "value").Type.Kinds.ToArray());
        Assert.Contains("number | ValueDto", SchemaExporter.Generate(graph, "typescript").Content);
        Assert.Contains(SchemaExporter.Generate(graph, "csharp").Diagnostics, item => item.Code == "csharp-dynamic");
    }

    [Fact]
    public void SpecialNamesAndTypeCollisions_CompileAndPreserveOriginalWireNames()
    {
        var result = Result("{\"a-b\":{\"x\":1},\"a_b\":{\"x\":\"s\"},\"Id\":1,\"id\":2,\"\":{},\"GeneratedSchema\":true,\"EqualityContract\":2,\"AdditionalProperties\":3,\"a.b\":4}");
        var code = SchemaExporter.GenerateCSharp(result);
        var assembly = Compile(code);
        var root = assembly.GetType("ObjectAnalyzer.Generated.GeneratedSchema")!;
        var instance = JsonSerializer.Deserialize("{\"a-b\":{\"x\":1},\"a_b\":{\"x\":\"s\"},\"Id\":1,\"id\":2,\"\":{},\"GeneratedSchema\":true,\"EqualityContract\":2,\"AdditionalProperties\":3,\"a.b\":4}", root);
        Assert.NotNull(instance);
        using var roundtrip = JsonDocument.Parse(JsonSerializer.Serialize(instance, root));
        Assert.True(roundtrip.RootElement.TryGetProperty("a.b", out _)); Assert.True(roundtrip.RootElement.TryGetProperty("", out _));
    }

    [Fact]
    public void OpenApi31_UsesNativeNullTypesAndAllReferencesResolve()
    {
        var graph = SchemaInference.Infer(Result("{\"a-b\":{\"x\":1},\"a_b\":{\"y\":true},\"value\":null,\"matrix\":[[{\"x\":1}]]}"));
        string json = SchemaExporter.Generate(graph, "openapi").Content;
        using var document = JsonDocument.Parse(json);
        Assert.Equal("3.1.0", document.RootElement.GetProperty("openapi").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                    if (property.Name == "$ref") Assert.True(schemas.TryGetProperty(property.Value.GetString()!.Split('/')[^1], out _)); else Check(property.Value);
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Check(item);
        }
        Check(document.RootElement);
        var yaml = new YamlStream(); yaml.Load(new StringReader(SchemaExporter.Generate(graph, "openapi-yaml").Content));
        Assert.Single(yaml.Documents);
    }

    [Fact]
    public void StrictMode_RejectsExplicitFallbacksAndOldFacadeEmbedsDiagnostics()
    {
        var graph = SchemaInference.Infer(Result("{\"x\":[[1],null]}"));
        Assert.Throws<SchemaExportException>(() => SchemaExporter.Generate(graph, "proto", new() { Strict = true }));
        var output = SchemaExporter.Generate(graph, "proto");
        Assert.Contains(output.Diagnostics, item => item.Code == "protobuf-json-text"); Assert.Contains("protobuf-json-text", output.Content);
    }

    [Fact]
    public void InferenceBudgets_AreCheckedForPathsAndNestedRetainedValues()
    {
        var result = Result("{\"a\":{\"b\":{\"c\":1}}}");
        Assert.Throws<AnalysisResourceLimitException>(() => SchemaInference.Infer(result, new() { MaxDepth = 1 }));
        Assert.Throws<AnalysisResourceLimitException>(() => SchemaInference.Infer(result, new() { MaxNodes = 2 }));
    }

    [Fact]
    public void SqliteDdl_ExecutesForUnionsEmptyObjectsAndIdentifierCollisions()
    {
        var graph = SchemaInference.Infer(Result("{\"a-b\":1,\"a_b\":2,\"id\":1,\"Id\":2,\"mixed\":1}", "{\"mixed\":\"s\"}"));
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = SchemaExporter.Generate(graph, "sqlite").Content; command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('GeneratedSchema')";
        Assert.Equal(5L, command.ExecuteScalar());
        command.CommandText = SchemaExporter.Generate(SchemaInference.Infer(Result("{}")), "sqlite", new() { RootName = "Empty" }).Content; command.ExecuteNonQuery();
    }

    [Fact]
    public void ExportBackends_DoNotMutateTheGraphOrRetainFieldStatisticReferences()
    {
        var source = Result("{\"x\":1}"); var graph = SchemaInference.Infer(source); string before = Json(graph);
        source.FieldStatistics.Clear();
        foreach (ISchemaBackend backend in new ISchemaBackend[] { new CSharpSchemaBackend(), new TypeScriptSchemaBackend(), new JsonSchemaBackend(), new OpenApiSchemaBackend(), new ProtobufSchemaBackend(), new SqlSchemaBackend() })
            _ = backend.Generate(graph, new());
        Assert.Equal(before, Json(graph)); Assert.NotNull(Property(graph.Root, "x"));
    }

    [Fact]
    public void DecimalRangeComparisonAndSqlScale_DoNotUseLexicalLengthOrAverages()
    {
        var graph = SchemaInference.Infer(Result("{\"x\":0.0015,\"exact\":0.1234567890123456789012345678}", "{\"x\":10,\"exact\":0.2234567890123456789012345678}"));
        Assert.Equal("0.0015", Property(graph.Root, "x").Type.Numeric!.Minimum);
        Assert.Equal("10", Property(graph.Root, "x").Type.Numeric!.Maximum);
        Assert.Contains("DECIMAL(28,28)", SchemaExporter.Generate(graph, "sqlserver").Content);
    }

    [Fact]
    public void GeneratedCSharp_PreservesArbitraryPrecisionJsonNumbersAtRuntime()
    {
        const string row = "{\"huge\":1e1000,\"precise\":0.1234567890123456789012345678}";
        var root = Compile(SchemaExporter.GenerateCSharp(Result(row))).GetType("ObjectAnalyzer.Generated.GeneratedSchema")!;
        var value = JsonSerializer.Deserialize(row, root);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, root));
        Assert.Equal("1e1000", document.RootElement.GetProperty("huge").GetRawText());
        Assert.Equal(0.1234567890123456789012345678m, document.RootElement.GetProperty("precise").GetDecimal());
    }

    [Fact]
    public void NativeBinaryEvidence_UsesBinarySqlTypesInsteadOfGuessingString()
    {
        var graph = SchemaInference.Infer(Analyze([new() { ["payload"] = new byte[] { 1, 2, 3 } }]));
        Assert.Contains("BYTEA", SchemaExporter.Generate(graph, "sql").Content);
        Assert.Contains("BLOB", SchemaExporter.Generate(graph, "sqlite").Content);
    }

    [Fact]
    public void SemanticHints_RequireCompleteMatchingEvidenceBeforeTypeNarrowing()
    {
        var source = Result("{\"stamp\":\"2026-08-08T12:00:00Z\"}", "{\"stamp\":\"2026-08-08T12:00:00Z\"}", "{\"stamp\":\"2026-08-08T12:00:00Z\"}", "{\"stamp\":\"2026-08-08T12:00:00Z\"}", "{\"stamp\":\"not a timestamp\"}");
        Assert.Equal("DateTime", source.FieldStatistics[0].SemanticType);
        var graph = SchemaInference.Infer(source);
        Assert.Empty(Property(graph.Root, "stamp").Type.VerifiedSemanticTypes);
        Assert.Contains("public string Stamp", SchemaExporter.Generate(graph, "csharp").Content);
        using var schema = JsonDocument.Parse(SchemaExporter.Generate(graph, "json-schema").Content);
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("stamp").TryGetProperty("format", out _));
    }

    [Fact]
    public void LocalClrTemporalValues_DoNotClaimRfc3339OrIsoDurationFormats()
    {
        var result = Analyze([new() { ["date"] = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Unspecified), ["time"] = new TimeOnly(12, 30), ["duration"] = TimeSpan.FromMinutes(1) }]);
        var output = SchemaExporter.Generate(result, "json-schema");
        Assert.Contains(output.Diagnostics, item => item.Code == "json-local-temporal-format");
        using var schema = JsonDocument.Parse(output.Content);
        foreach (var property in schema.RootElement.GetProperty("properties").EnumerateObject()) Assert.False(property.Value.TryGetProperty("format", out _));
    }

    [Fact]
    public void EnumConstraints_AreExpressedOrDiagnosedInsteadOfSilentlyDropped()
    {
        var source = Result("{\"status\":\"active\"}");
        source.FieldStatistics[0].AppliedRule = new() { EnumValues = [JsonSerializer.SerializeToElement("active"), JsonSerializer.SerializeToElement("inactive")] };
        var graph = SchemaInference.Infer(source);
        Assert.Contains("(string) & (\"active\" | \"inactive\")", SchemaExporter.Generate(graph, "typescript").Content);
        Assert.Contains(SchemaExporter.Generate(graph, "csharp").Diagnostics, item => item.Code == "csharp-enum-unenforced");
        Assert.Contains(SchemaExporter.Generate(graph, "proto").Diagnostics, item => item.Code == "protobuf-enum-unenforced");
    }

    [Fact]
    public void RedactedComplexExamples_AreNotUsedToGuessOriginalNestedTypes()
    {
        var rules = new AnalysisRules { Fields = { ["profile.token"] = new() { Pii = new() { Mask = new() { MaskAll = true } } } } };
        var source = Analyze([new() { ["profile"] = new Dictionary<string, object?> { ["token"] = 1234 } }], rules);
        var graph = SchemaInference.Infer(ResultProjection.Create(source).Result);
        Assert.True(Property(graph.Root, "profile").Type.ShapeIncomplete);
        Assert.Empty(Property(graph.Root, "profile").Type.Properties);
        Assert.Contains(graph.Diagnostics, item => item.Code == "redacted-evidence");
    }

    [Fact]
    public void ExternalCompilerFixture_GeneratesEveryFormatAndCompilesCSharp()
    {
        const string rows = """
        [{"id":1,"Id":2,"a-b":{"name":"a"},"a_b":{"name":1},"matrix":[[1,null],[2]],"mixed":[1,"x",{"left":true},null],"empty":[],"nullable":null,"a.b":"literal","a":{"b":"nested"},"":true,"quote\"line\n":"safe","message":2,"GeneratedSchema":5},
         {"id":2,"Id":3,"a-b":{"name":"b"},"a_b":{"name":2},"matrix":[[]],"mixed":[{"right":4},false],"empty":[],"nullable":"present","a.b":"literal","a":{"b":"nested"},"":false,"quote\"line\n":"other","message":3,"GeneratedSchema":6}]
        """;
        using var json = JsonDocument.Parse(rows);
        var source = Result(json.RootElement.EnumerateArray().Select(item => item.GetRawText()).ToArray());
        var graph = SchemaInference.Infer(source);
        string code = SchemaExporter.Generate(graph, "csharp").Content; _ = Compile(code);
        string? directory = Environment.GetEnvironmentVariable("OBJECT_ANALYZER_SCHEMA_EXPORT_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var pair in new[] { ("csharp", "model.cs"), ("typescript", "model.ts"), ("json-schema", "model.schema.json"), ("openapi", "model.openapi.json"), ("openapi-yaml", "model.openapi.yaml"), ("proto", "model.proto"), ("sqlite", "model.sqlite.sql") })
            File.WriteAllText(Path.Combine(directory, pair.Item2), SchemaExporter.Generate(graph, pair.Item1).Content);
        File.WriteAllText(Path.Combine(directory, "samples.json"), rows);
        File.WriteAllText(Path.Combine(directory, "instances.ts"), "import type { GeneratedSchema } from './model';\nconst samples: GeneratedSchema[] = " + rows + ";\nvoid samples;\n");
    }

    private static System.Reflection.Assembly Compile(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("SchemaValidation_" + Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(code)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream(); var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error);
        return System.Reflection.Assembly.Load(output.ToArray());
    }
}
