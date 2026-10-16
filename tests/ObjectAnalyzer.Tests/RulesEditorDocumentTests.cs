using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Tests;

public sealed class RulesEditorDocumentTests
{
    [Theory]
    [InlineData("-12", true, true)]
    [InlineData("", true, true)]
    [InlineData("1.5", true, false)]
    [InlineData(".25", false, true)]
    [InlineData("-1.2e-3", false, true)]
    [InlineData("1e-", false, true)]
    [InlineData("e", false, false)]
    [InlineData("1 2", false, false)]
    [InlineData("abc", false, false)]
    [InlineData("NaN", false, false)]
    [InlineData("1.2.3", false, false)]
    public void NumericInput_RestrictsCharactersAndAllowsEditing(string value, bool wholeNumbersOnly, bool accepted) =>
        Assert.Equal(accepted, RulesEditorOption.AcceptsNumericInput(value, wholeNumbersOnly));

    [Fact]
    public void TypedEditors_ExposeChoicesAndPreserveImportedExtensions()
    {
        var field = new RulesEditorField();
        var mode = field.Options.Single(o => o.Path == "transform.mode");
        Assert.True(mode.HasChoices);
        Assert.Contains(mode.Choices, c => c.Value == "verify");
        Assert.Contains(mode.Choices, c => c.Value == "cbc");
        var type = field.Options.Single(o => o.Path == "primaryType");
        type.Text = "CustomType";
        Assert.Contains(type.Choices, c => c.Value == "CustomType");
        var number = field.Options.Single(o => o.Path == "maxOutlierRatio");
        Assert.True(number.IsNumeric);
        Assert.False(number.HasChoices);
    }

    [Fact]
    public void NumericOptions_AcceptDecimalAndScientificFormsAndRejectOverflow()
    {
        var document = RulesEditorDocument.Parse("{}");
        var width = document.RuntimeOptions.Single(o => o.Path == "distribution.lowConfidenceWidth");
        width.Text = ".25";
        Assert.Equal(0.25, RulesEditorDocument.Validate(document.ToJson()).Runtime!["distribution"]!["lowConfidenceWidth"]!.GetValue<double>());
        width.Text = "1e-2";
        Assert.Equal(0.01, RulesEditorDocument.Validate(document.ToJson()).Runtime!["distribution"]!["lowConfidenceWidth"]!.GetValue<double>());
        width.Text = "1e999";
        Assert.Throws<InvalidOperationException>(() => document.ToJson());
    }

    [Fact]
    public void EditingOtherSettings_PreservesEmptyPiiConfiguration()
    {
        var document = RulesEditorDocument.Parse("""{"fields":{"id":{"pii":{"mask":{}}}}}""");
        document.Fields[0].Required = true;
        var rules = RulesEditorDocument.Validate(document.ToJson());
        Assert.NotNull(rules.Fields["id"].Pii);
        Assert.NotNull(rules.Fields["id"].Pii!.Mask);
    }

    [Fact]
    public void AllRuntimeKeys_AreExposedAndAppliedToCore()
    {
        var document = RulesEditorDocument.Parse("{}");
        var expected = RulesToOptionsConverter.KnownRuntimeKeys.SelectMany(p =>
            p.Value is null ? new[] { p.Key } : p.Value.Select(c => p.Key + "." + c)).Where(p => p != "flatten");
        Assert.Equal(expected.Order(), document.RuntimeOptions.Select(o => o.Path).Order());
        document.RuntimeOptions.Single(o => o.Path == "sampling.step").Text = "5";
        document.RuntimeOptions.Single(o => o.Path == "deepAnalysis.correlation").Text = "true";
        document.RuntimeOptions.Single(o => o.Path == "selectedFields").Text = "[\"age\",\"name\"]";
        var options = new AnalysisOptions();
        RulesToOptionsConverter.Apply(options, RulesEditorDocument.Validate(document.ToJson()));
        Assert.Equal(5, options.Sampling.SampleStep);
        Assert.True(options.Advanced.EnableCorrelation);
        Assert.Equal("age,name", options.FieldSelection.Fields);
    }

    [Fact]
    public void NestedFieldOptionsAndParsers_RoundTripAndCanBeRemoved()
    {
        var document = RulesEditorDocument.Parse("{}");
        var field = new RulesEditorField { Name = "secret" };
        document.Fields.Add(field);
        field.Options.Single(o => o.Path == "pii.mask.keepPrefix").Text = "4";
        field.Options.Single(o => o.Path == "transform.parse").Text = "json";
        field.Options.Single(o => o.Path == "enumValues").Text = "[1,\"two\"]";
        field.Options.Single(o => o.Path == "redact").Text = "false";
        var parser = new RulesEditorParser();
        parser.Options.Single(o => o.Path == "source").Text = "payload";
        parser.Options.Single(o => o.Path == "parseType").Text = "json";
        parser.Options.Single(o => o.Path == "header").Text = "0";
        document.ValueParsers.Add(parser);
        var rules = RulesEditorDocument.Validate(document.ToJson());
        Assert.Equal(4, rules.Fields["secret"].Pii!.Mask!.KeepPrefix);
        Assert.False(rules.Fields["secret"].Redact);
        Assert.Equal("json", rules.Fields["secret"].Transform!.Parse);
        Assert.Equal(2, rules.Fields["secret"].EnumValues!.Count);
        Assert.Equal(0, Assert.Single(rules.ValueParsers!).Header);
        var reloaded = RulesEditorDocument.Parse(document.ToJson());
        Assert.Equal(document.FormFingerprint(), reloaded.FormFingerprint());
        reloaded.ValueParsers.Clear();
        reloaded.Fields[0].Options.Single(o => o.Path == "pii.mask.keepPrefix").Text = "";
        rules = RulesEditorDocument.Validate(reloaded.ToJson());
        Assert.Empty(rules.ValueParsers!);
        Assert.Null(rules.Fields["secret"].Pii);
    }

    [Theory]
    [InlineData("sampling.step", "1.5")]
    [InlineData("deepAnalysis.correlation", "\"true\"")]
    [InlineData("selectedFields", "[1]")]
    public void RuntimeForms_RejectWrongTypes(string path, string value)
    {
        var document = RulesEditorDocument.Parse("{}");
        document.RuntimeOptions.Single(o => o.Path == path).Text = value;
        Assert.Throws<InvalidOperationException>(() => document.ToJson());
    }

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
