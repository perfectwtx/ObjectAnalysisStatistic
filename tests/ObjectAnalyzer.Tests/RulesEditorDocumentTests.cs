using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Tests;

public sealed class RulesEditorDocumentTests
{
    [Fact]
    public void Template_IsValidAndReadableByCore()
    {
        var rules = RulesEditorDocument.Validate(RulesEditorDocument.Template);
        Assert.Equal(7, rules.Fields.Count);
        Assert.Equal("json", rules.Fields["line"].Transform!.Parse);
        Assert.Single(rules.ValueParsers!);
        Assert.Equal(3, rules.Expectations!.MinRowCount);
    }

    [Fact]
    public void RenameAndSwapFields_PreservesAdvancedRulesByRowIdentity()
    {
        var document = RulesEditorDocument.Parse("""
            {"fields":{
              "first":{"enumValues":[1,2],"transform":{"parse":"json"},"pii":{"label":"secret"}},
              "second":{"defaultValue":"other","redact":false}
            },"runtime":{"flatten":false,"sampling":{"step":5}},
              "valueParsers":[{"source":"payload","parseType":"json"}]}
            """);
        document.Fields[0].Name = "second";
        document.Fields[1].Name = "first";
        var rules = RulesEditorDocument.Validate(document.ToJson());
        Assert.Equal(2, rules.Fields["second"].EnumValues!.Count);
        Assert.Equal("json", rules.Fields["second"].Transform!.Parse);
        Assert.Equal("secret", rules.Fields["second"].Pii!.Label);
        Assert.False(rules.Fields["first"].Redact);
        Assert.Equal("other", ((JsonElement)rules.Fields["first"].DefaultValue!).GetString());
        Assert.Equal(5, rules.Runtime!["sampling"]!["step"]!.GetValue<int>());
        Assert.Single(rules.ValueParsers!);
    }

    [Fact]
    public void DeleteAndAddFields_RemovesEntireRuleWithoutRestoringItLater()
    {
        var document = RulesEditorDocument.Parse("""{"fields":{"old":{"enumValues":["a"]}}}""");
        document.Fields.Clear();
        document.Fields.Add(new RulesEditorField { Name = "new", Required = true });
        var rules = RulesEditorDocument.Validate(document.ToJson());
        Assert.False(rules.Fields.ContainsKey("old"));
        Assert.True(rules.Fields["new"].Required);
        Assert.Null(rules.Fields["new"].EnumValues);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("same")]
    public void InvalidOrDuplicateFieldName_CannotOverwriteExistingRule(string name)
    {
        var document = RulesEditorDocument.Parse("""{"fields":{"same":{"required":true}}}""");
        document.Fields.Add(new RulesEditorField { Name = name });
        Assert.Throws<InvalidOperationException>(() => document.ToJson());
        // A failed sync does not mutate the source or drop the original rule.
        document.Fields.RemoveAt(1);
        Assert.True(RulesEditorDocument.Validate(document.ToJson()).Fields["same"].Required);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"fields\":[]}")]
    [InlineData("{\"fields\":null}")]
    [InlineData("{\"fields\":{\"a\":null}}")]
    [InlineData("{\"fields\":{\"a\":1}}")]
    [InlineData("{\"runtime\":{\"flatten\":\"false\"}}")]
    public void InvalidStructure_CannotBecomeAnEmptyForm(string text)
    {
        Assert.ThrowsAny<Exception>(() => RulesEditorDocument.Parse(text));
    }

    [Theory]
    [InlineData("{\"fields\":{\"a\":{\"minValue\":2,\"maxValue\":1}}}")]
    [InlineData("{\"fields\":{\"a\":{\"nullRateMax\":1.1}}}")]
    [InlineData("{\"fields\":{\"a\":{\"maxOutlierRatio\":-0.1}}}")]
    [InlineData("{\"fields\":{\"a\":{\"pattern\":\"[\"}}}")]
    [InlineData("{\"expectations\":{\"minRowCount\":-1}}")]
    [InlineData("{\"expectations\":{\"maxDuplicateRate\":2}}")]
    [InlineData("{\"runtime\":{\"flaten\":true}}")]
    public void InvalidSemantics_AreRejectedInAdvancedJsonAndForms(string text)
    {
        Assert.Throws<InvalidOperationException>(() => RulesEditorDocument.Validate(text));
    }

    [Fact]
    public void Expectations_UseCoreInt64RangeAndInvariantNumbers()
    {
        var document = RulesEditorDocument.Parse("{}");
        document.MinRowCount = "3000000000";
        document.MaxDuplicateRate = "0.25";
        document.Fields.Add(new RulesEditorField { Name = "number", MinValue = "1.25", MaxValue = "2e3" });
        var rules = RulesEditorDocument.Validate(document.ToJson());
        Assert.Equal(3000000000L, rules.Expectations!.MinRowCount);
        Assert.Equal(0.25, rules.Expectations.MaxDuplicateRate);
        Assert.Equal(1.25, rules.Fields["number"].MinValue);
        Assert.Equal(2000, rules.Fields["number"].MaxValue);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("not-a-number")]
    public void NonFiniteOrInvalidFormNumber_IsRejected(string value)
    {
        var document = RulesEditorDocument.Parse("{}");
        document.Fields.Add(new RulesEditorField { Name = "a", MinValue = value });
        Assert.Throws<InvalidOperationException>(() => document.ToJson());
    }

    [Fact]
    public void JsoncCommentsAndTrailingCommas_AreConsumedByTheActualCoreOptions()
    {
        const string text = """
            {
              // Comments may contain quotes and commas: ", } ",
              "runtime": {"flatten": false, /* trailing */},
              "fields": {"x": {"pattern": "a,b",},},
              "expectations": {"minRowCount": 1,},
            }
            """;
        var rules = RulesEditorDocument.Validate(text);
        var coreRules = JsonSerializer.Deserialize<AnalysisRules>(text, AnalysisRules.RulesJsonOptions)!;
        var options = new AnalysisOptions();
        RulesToOptionsConverter.Apply(options, coreRules);
        Assert.False(options.Parse.Flatten);
        Assert.Equal(rules.Fields["x"].Pattern, coreRules.Fields["x"].Pattern);
    }

    [Fact]
    public void CaseInsensitivePropertyNames_DoNotLoseExistingRules()
    {
        var document = RulesEditorDocument.Parse("""
            {"Fields":{"a":{"Required":true,"MinValue":1}},"Runtime":{"Flatten":false}}
            """);
        Assert.False(document.Flatten);
        Assert.True(document.Fields[0].Required);
        document.Fields[0].Name = "renamed";
        var root = JsonNode.Parse(document.ToJson())!;
        Assert.Null(root["Fields"]);
        Assert.NotNull(root["fields"]!["renamed"]);
        Assert.True(RulesEditorDocument.Validate(document.ToJson()).Fields["renamed"].Required);
    }

    [Fact]
    public void FormSync_PreservesUnknownPropertiesForRepairInAdvancedEditor()
    {
        var document = RulesEditorDocument.Parse("""{"fields":{"a":{"requird":true}}}""");
        document.Fields[0].Name = "renamed";
        var text = document.ToJson();
        Assert.True(JsonNode.Parse(text)!["fields"]!["renamed"]!["requird"]!.GetValue<bool>());
        Assert.Throws<InvalidOperationException>(() => RulesEditorDocument.Validate(text));
    }

    [Fact]
    public void EditingOtherValues_PreservesWhitespaceRegex()
    {
        var document = RulesEditorDocument.Parse("""{"fields":{"a":{"pattern":" "}}}""");
        document.Fields[0].Required = true;
        Assert.Equal(" ", RulesEditorDocument.Validate(document.ToJson()).Fields["a"].Pattern);
    }

    [Fact]
    public void FormFingerprint_ChangesForEditsButNotJsonFormatting()
    {
        var document = RulesEditorDocument.Parse("{\"fields\":{\"a\":{}}}");
        var fingerprint = document.FormFingerprint();
        Assert.Equal(fingerprint, RulesEditorDocument.Parse("{ /* comment */ \"fields\": {\"a\": {},},}").FormFingerprint());
        document.Fields[0].Name = "renamed";
        Assert.NotEqual(fingerprint, document.FormFingerprint());
    }
}
