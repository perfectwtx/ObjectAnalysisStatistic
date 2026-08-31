using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 规则文本的未知键检查：拼错 / 已废弃的键在反序列化时会被 STJ 静默忽略，
/// 靠这里把它们揪出来。API（/api/rules/validate、/api/analyze）与 CLI
/// （AnalysisRunner.LoadRules）共用同一份实现。
/// </summary>
public class RulesLintTests
{
    [Fact]
    public void Scan_TypoInFieldRule_ReportsPathAndSuggestion()
    {
        var unknowns = RulesLint.Scan("""{"fields":{"age":{"minValue2":0,"requird":true}}}""");

        Assert.Equal(2, unknowns.Count);
        Assert.All(unknowns, u => Assert.Equal("fields.age", u.Path));
        Assert.Contains(unknowns, u => u.Name == "minValue2" && u.Suggestion == "minValue");
        Assert.Contains(unknowns, u => u.Name == "requird" && u.Suggestion == "required");
    }

    [Fact]
    public void Scan_UnknownTopLevelKey_UsesRootPath()
    {
        var unknowns = RulesLint.Scan("""{"flaten":true}""");

        var hit = Assert.Single(unknowns);
        Assert.Equal("$", hit.Path);
        Assert.Equal("flatten", hit.Suggestion);
    }

    [Fact]
    public void Scan_ValidRules_ReturnsEmpty()
    {
        var unknowns = RulesLint.Scan("""
        {
          // 注释与尾随逗号都不该干扰扫描
          "flatten": true,
          "fields": {
            "age": { "required": true, "minValue": 0, "maxValue": 120 },
            "email": { "pattern": "\\S+@\\S+", "transform": { "parse": "jwt" } }
          },
          "expectations": { "minRowCount": 3 }
        }
        """);

        Assert.Empty(unknowns);
    }

    [Fact]
    public void Scan_MalformedJson_ReturnsEmpty()
    {
        // 语法错误由反序列化负责报错，这里不重复报
        Assert.Empty(RulesLint.Scan("{ not valid"));
        Assert.Empty(RulesLint.Scan(""));
    }

    [Fact]
    public void Scan_FullReferenceTemplate_HasNoUnknowns()
    {
        // 参考模板覆盖全部规则键：若有未知项说明反射取属性名取错了，会误伤所有用户
        var path = Path.Combine(FindRepoRoot(), "samples", "rules_full_reference.jsonc");
        Assert.True(File.Exists(path), $"找不到参考模板: {path}");

        Assert.Empty(RulesLint.Scan(File.ReadAllText(path)));
    }

    [Fact]
    public void Scan_RuntimeSection_RecognizesAllKeys()
    {
        // 方案 A：runtime 节点下的所有子节点键都应该被识别
        var unknowns = RulesLint.Scan("""
        {
          "runtime": {
            "flatten": true,
            "csvInferNumbers": false,
            "maxValuesToShow": 10,
            "maxParallelism": 4,
            "allowUnknownRules": true,
            "selectedFields": ["a", "b"],
            "filter": "age >= 0",
            "sampling": { "reservoir": 100, "step": 5 },
            "jsonPath": { "root": "$.data[*]", "record": "$.payload" },
            "schema": { "snapshot": "snap.json", "diffBaseline": "base.json", "diffReport": "diff.json", "allowBreakingChanges": false },
            "distribution": { "snapshot": "d.json", "diffBaseline": "db.json", "diffReport": "dr.json", "lowConfidenceWidth": 0.1 },
            "pii": { "off": false, "report": "pii.json", "failOnLevel": "B" },
            "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.1 },
            "p4": { "correlation": true, "stringPatterns": true, "unicodeHygiene": false, "timeSeries": true, "distributionSnapshot": true, "fuzzyDedupField": "name" },
            "checkpoint": { "path": "ck.json", "resumeFrom": "r.json", "baseline": "b.json" },
            "memory": { "maxMB": 1024, "mode": "hard" },
            "progress": { "enabled": false },
            "audit": { "log": "audit.ndjson", "operator": "ci-bot" },
            "outputs": { "primary": "out.html", "violations": "v.json", "quality": "q.json", "schemaCode": "csharp:out.cs" }
          }
        }
        """);

        Assert.Empty(unknowns);
    }

    [Fact]
    public void Scan_RuntimeSection_TypoIsReportedWithPath()
    {
        // runtime.pii.failOnLevle 拼错 → 应被识别为未知键
        var unknowns = RulesLint.Scan("""
        {
          "runtime": {
            "pii": { "failOnLevle": "B" }
          }
        }
        """);

        var hit = Assert.Single(unknowns);
        Assert.Equal("runtime.pii", hit.Path);
        Assert.Equal("failOnLevle", hit.Name);
        Assert.Equal("failOnLevel", hit.Suggestion);
    }

    [Fact]
    public void Describe_ListsAtMostThreeAndTotalCount()
    {
        var unknowns = RulesLint.Scan("""
        {"fields":{"a":{"aaa1":1},"b":{"bbb1":1},"c":{"ccc1":1},"d":{"ddd1":1}}}
        """);
        Assert.Equal(4, unknowns.Count);

        var message = RulesLint.Describe(unknowns);
        Assert.Contains("aaa1", message);
        Assert.Contains("ccc1", message);
        Assert.DoesNotContain("ddd1", message); // 第 4 项只计数量不点名
        Assert.Contains("共 4 项", message);
        Assert.Contains("--allow-unknown-rules", message);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
