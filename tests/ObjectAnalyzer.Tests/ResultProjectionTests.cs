using System.Text.Json;
using ObjectAnalyzer.Data;
using ObjectAnalyzer.Platform;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Tests;

public sealed class ResultProjectionTests
{
    private const string Secret = "sensitive-secret-93841";
    private static FieldRule Private(bool all = true) => new() { Pii = new() { Mask = new() { MaskAll = all, KeepPrefix = 2, KeepSuffix = 2 } } };
    private static AnalysisResult Sensitive(string path = "token") => Analyze(
        [new Dictionary<string, object?> { [path] = Secret }], new AnalysisRules { Fields = { [path] = Private() } });
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Projection_DetachesAllMutableMembersAndPreservesTheOriginal()
    {
        var source = Sensitive(); string before = Json(source);
        var delivery = ResultProjection.Create(source);
        delivery.Result.FieldStatistics[0].TypeCounts["probe"] = 1;
        delivery.Result.Reliability!.Limitations.Add(new("probe", AnalysisAccuracy.Exact, "probe"));
        delivery.Result.PrivacyDirectives!.Clear();
        Assert.Equal(before, Json(source));
        Assert.DoesNotContain(Secret, Json(delivery));
    }

    [Fact]
    public void NestedRules_AreAvailableWithoutFlatteningOrAnObservedLeafStatistic()
    {
        var rules = new AnalysisRules { Fields = { ["profile.token"] = Private() } };
        var row = new Dictionary<string, object?> { ["profile"] = new Dictionary<string, object?> { ["token"] = Secret, ["city"] = "Paris" } };
        var source = Analyze([row], rules);
        Assert.Equal("profile", Assert.Single(source.FieldStatistics).FieldName);
        var delivery = ResultProjection.Create(source, [row]);
        Assert.DoesNotContain(Secret, Json(delivery));
        Assert.Contains("Paris", Json(delivery));
        Assert.Contains(Secret, Json(source));
    }

    [Fact]
    public void StructuredPaths_DistinguishLiteralDotsEmptyNamesAndArrayIndices()
    {
        var rules = new AnalysisRules { Fields =
        {
            ["[\"a.b\"]"] = Private(), ["users[1].token"] = Private(), ["[\"\"]"] = Private()
        } };
        var row = new Dictionary<string, object?>
        {
            ["[\"a.b\"]"] = Secret, ["[\"\"]"] = Secret,
            ["a"] = new Dictionary<string, object?> { ["b"] = "public" },
            ["users"] = new object[] { new Dictionary<string, object?> { ["token"] = "public" }, new Dictionary<string, object?> { ["token"] = Secret } }
        };
        var result = ResultProjection.Create(Analyze([row], rules), [row]);
        Assert.DoesNotContain(Secret, Json(result)); Assert.Contains("public", Json(result));
    }

    [Fact]
    public void ParentMask_InheritsThroughArraysAndHonorsMoreSpecificExemption()
    {
        var rules = new AnalysisRules { Fields = { ["account"] = Private(), ["account.public"] = new() { Redact = false } } };
        var row = new Dictionary<string, object?> { ["account"] = new Dictionary<string, object?> { ["public"] = "visible", ["tokens"] = new object[] { Secret, Secret } } };
        var projected = ResultProjection.Create(Analyze([row], rules), [row]);
        Assert.DoesNotContain(Secret, Json(projected)); Assert.Contains("visible", Json(projected));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitParentPolicy_IsNotWeakenedByAutomaticallyInferredLeaf(bool exempt)
    {
        var rules = new AnalysisRules { Fields = { ["profile"] = exempt ? new() { Redact = false } : Private() } };
        var source = Analyze([new() { ["profile.Phone"] = "13812341234" }], rules);
        var projected = ResultProjection.Create(source).Result;
        string value = Assert.Single(projected.FieldStatistics[0].ValueCounts).Key.ToDisplayString();
        Assert.Equal(exempt ? "13812341234" : "***********", value);
    }

    [Fact]
    public void NumericAndBooleanValues_AreMaskedButRetainSourceTypeAndCountInSharedView()
    {
        var rules = new AnalysisRules { Fields = { ["token"] = Private() } };
        var source = Analyze([new() { ["token"] = 1234 }, new() { ["token"] = "1234" }, new() { ["token"] = true }], rules);
        var projected = ResultProjection.Create(source).Result;
        var views = ResultProjection.Values(Assert.Single(projected.FieldStatistics));
        Assert.Contains(views, item => item.Kind == RecordValueKind.Number && item.Count == 1 && item.Redacted);
        Assert.Contains(views, item => item.Kind == RecordValueKind.String && item.Count == 1 && item.Redacted);
        Assert.Contains(views, item => item.Kind == RecordValueKind.Boolean && item.Count == 1 && item.Redacted);
        Assert.Equal(3, views.Sum(item => item.Count));
        Assert.Null(projected.FieldStatistics[0].MinNumber);
        Assert.DoesNotContain("1234", Json(projected));
    }

    [Fact]
    public void DuplicateJson_RecursesIntoObjectsAndMasksNumericScalars()
    {
        var source = Sensitive("profile.token");
        source.DuplicateObjects = [new() { Count = 2, ObjectJson = "{\"profile\":{\"token\":123456789},\"plain\":7}" }];
        var projected = ResultProjection.Create(source).Result;
        Assert.DoesNotContain("123456789", Json(projected));
        using var document = JsonDocument.Parse(projected.DuplicateObjects[0].ObjectJson);
        Assert.Equal(7, document.RootElement.GetProperty("plain").GetInt32());
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("profile").GetProperty("token").ValueKind);
    }

    [Fact]
    public void InvalidDuplicateJson_IsHiddenInsteadOfBeingPassedThrough()
    {
        var source = Sensitive(); source.DuplicateObjects = [new() { Count = 2, ObjectJson = Secret }];
        Assert.Equal("[redacted]", ResultProjection.Create(source).Result.DuplicateObjects[0].ObjectJson);
    }

    [Fact]
    public void FuzzyAndPatternCarriers_UseTheirFieldProvenanceAndHideLiteralTemplates()
    {
        var source = Sensitive();
        source.FuzzyDuplicates = new() { Field = "token", TotalSamples = 2, GroupCount = 1, Groups = [new() { Count = 2, CanonicalKey = Secret, RepresentativeJson = Secret, Examples = [Secret] }] };
        source.StringPatterns = new() { ["token"] = new() { TotalSamples = 2, DominantPattern = new() { Template = Secret, Examples = [Secret], Count = 2 }, Patterns = [new() { Template = Secret, Examples = [Secret], Count = 2 }] } };
        var projected = ResultProjection.Create(source).Result;
        Assert.DoesNotContain(Secret, Json(projected));
        Assert.Equal(2, projected.FuzzyDuplicates!.Groups[0].Count);
        Assert.Equal(2, projected.StringPatterns!["token"].Patterns[0].Count);
        source.FuzzyDuplicates.Field = null;
        var unknown = ResultProjection.Create(source).Result;
        Assert.Null(unknown.FuzzyDuplicates);
        Assert.Contains("fuzzy-values-without-field", unknown.Projection!.Omitted);
    }

    [Fact]
    public void RulesMessagesDefaultsAndExtensionPayloads_CannotLeakSensitiveLiteralsOrSecrets()
    {
        var source = Sensitive(); var field = source.FieldStatistics[0];
        field.AppliedRule!.DefaultValue = Secret; field.AppliedRule.EnumValues = [JsonSerializer.SerializeToElement(Secret)];
        field.AppliedRule.Pattern = Secret; field.AppliedRule.Transform = new() { Type = "aes", Key = Secret, PrivateKey = Secret };
        field.AnalyzerResults = new() { ["custom"] = JsonSerializer.SerializeToElement(Secret) };
        field.ValueRuleResults = new() { ["custom"] = new() { Count = 2, Samples = [Secret], Message = Secret } };
        source.QualityViolations = [new() { Field = "token", Count = 2, Check = "custom", Samples = [Secret], Message = Secret }];
        source.FilterExpression = Secret;
        var projected = ResultProjection.Create(source).Result;
        Assert.DoesNotContain(Secret, Json(projected));
        Assert.Equal(2, projected.QualityViolations[0].Count);
        Assert.Null(projected.FieldStatistics[0].AppliedRule!.Transform);
        Assert.Contains("analyzer-payloads", projected.Projection!.Omitted);
        Assert.Contains(Secret, Json(source));
    }

    [Fact]
    public void OpaquePayloadAndCredentials_AreOmittedEvenWhenNoFieldIsDetectedAsPii()
    {
        var source = Analyze([new() { ["x"] = 1 }]);
        source.FieldStatistics[0].AnalyzerResults = new() { ["custom"] = JsonSerializer.SerializeToElement(Secret) };
        source.FieldStatistics[0].AppliedRule = new() { Transform = new() { Key = Secret }, Checks = new() { ["custom"] = JsonSerializer.SerializeToElement(Secret) } };
        Assert.DoesNotContain(Secret, Json(ResultProjection.Create(source).Result));
    }

    [Fact]
    public void Projection_IsIdempotentIncludingOriginalKindsAndOmissionEvidence()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = Private(false) } };
        rules.Fields["x"].Pii!.Mask!.MaskChar = "ab";
        var source = Analyze([new() { ["x"] = 12345678 }, new() { ["x"] = "ABCDEFGH" }], rules);
        var first = ResultProjection.Create(source).Result;
        var second = ResultProjection.Create(first).Result;
        Assert.Equal(Json(first), Json(second));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("123")]
    [InlineData("12345678")]
    public void ShortAndCustomMasks_ChangeTheValueAndAreIdempotent(string value)
    {
        var mask = new PiiMaskFormat { IsCustom = true, KeepPrefix = 3, KeepSuffix = 4, MaskChar = "ab" };
        string once = mask.Apply(value);
        Assert.NotEqual(value, once); Assert.Equal(value.Length, once.Length); Assert.Equal(once, mask.Apply(once));
    }

    [Fact]
    public void UnsupportedValuesAndUnauditedMembers_FailClosed()
    {
        Assert.Throws<NotSupportedException>(() => PreviewRedactor.Redact([new() { ["x"] = new object() }], PiiPolicy.Empty));
        Assert.Throws<NotSupportedException>(() => ResultProjection.Create(new DerivedResult()));
        ResultProjectionContract.Validate();
        Assert.Throws<InvalidOperationException>(() => ResultProjectionContract.ValidateMembers(typeof(DerivedResult), ResultProjectionContract.Members[typeof(AnalysisResult)].Split(' ')));
        Assert.Throws<InvalidOperationException>(() => ResultProjectionContract.ValidateMembers(typeof(AnalysisResult), ["TotalObjects"]));
    }

    [Fact]
    public void HistoricalDelivery_ProjectsACopyWithoutChangingStoredResultJson()
    {
        var source = Sensitive(); var stored = Json(source);
        var job = new AnalysisJob { ResultJson = stored };
        var delivery = ResultProjection.Job(job);
        Assert.Equal(stored, job.ResultJson); Assert.DoesNotContain(Secret, delivery.ResultJson!);
        var restored = JsonSerializer.Deserialize<AnalysisResult>(stored)!;
        Assert.Contains(Secret, Json(restored));
    }

    [Fact]
    public void ExplicitRawDelivery_IsStillDetached()
    {
        var source = Sensitive(); var projected = ResultProjection.Create(source, redact: false);
        Assert.False(projected.Redacted); Assert.NotSame(source, projected.Result); Assert.Contains(Secret, Json(projected.Result));
    }

    private sealed class DerivedResult : AnalysisResult
    {
        public string UnauditedValue { get; set; } = Secret;
    }
}
