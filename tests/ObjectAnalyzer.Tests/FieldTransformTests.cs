using System.Text;
using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 字段选择与值转换功能测试：--fields 字段选择、规则文件 transform（JWT / AES / Base64 / URL）、转换失败记录。
/// </summary>
public class FieldTransformTests
{
    private static readonly string[] NameAndAgeFields = { "Name", "Age" };
    private static readonly string[] NameOnlyField = { "Name" };

    // ──────────────────────────────────────────────
    // 字段选择 (field selection)
    // ──────────────────────────────────────────────

    [Fact]
    public void Wrap_SelectedFields_OnlyKeepsSpecifiedFields()
    {
        var processor = new FieldTransformProcessor(null, NameAndAgeFields);

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            new() { { "Name", "Alice" }, { "Age", 30 }, { "Id", 1 } },
            new() { { "Name", "Bob" }, { "Age", 25 }, { "Id", 2 } }
        }).ToList();

        Assert.Equal(2, wrapped.Count);
        Assert.All(wrapped, d =>
        {
            Assert.Contains("Name", d.Keys);
            Assert.Contains("Age", d.Keys);
            Assert.DoesNotContain("Id", d.Keys);
        });
    }

    [Fact]
    public void Rules_SelectedFields_UsedWhenNoCliFields()
    {
        var rules = new AnalysisRules { SelectedFields = new List<string> { "Name" } };
        var processor = new FieldTransformProcessor(rules);

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            new() { { "Name", "Alice" }, { "Age", 30 } }
        }).ToList();

        Assert.Single(wrapped[0]);
        Assert.Contains("Name", wrapped[0].Keys);
    }

    [Fact]
    public void Analyze_WithFieldsSelection_StatsOnlySelectedFields()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Name", "Alice" }, { "Age", 30 } },
            new() { { "Name", "Bob" }, { "Age", 25 } }
        };

        var processor = new FieldTransformProcessor(null, NameOnlyField);
        var result = Analyze(processor.Wrap(objects));

        Assert.Equal(2, result.TotalObjects);
        var field = Assert.Single(result.FieldStatistics);
        Assert.Equal("Name", field.FieldName);
    }

    // ──────────────────────────────────────────────
    // Base64 解码
    // ──────────────────────────────────────────────

    [Fact]
    public void Transform_Base64_DecodesValue()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "encoded", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello world")) } },
            new() { { "encoded", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello world")) } }
        };

        var rules = new AnalysisRules();
        rules.Fields["encoded"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "encoded");
        Assert.Equal("hello world", stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
        Assert.Equal("base64", stat.TransformType);
    }

    // ──────────────────────────────────────────────
    // URL 解码
    // ──────────────────────────────────────────────

    [Fact]
    public void Transform_Url_DecodesValue()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "link", "https://example.com/path%20with%20spaces?q=1%2B2" } }
        };

        var rules = new AnalysisRules();
        rules.Fields["link"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "url" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "link");
        Assert.Equal("https://example.com/path with spaces?q=1+2", stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
    }

    // ──────────────────────────────────────────────
    // JWT 解码
    // ──────────────────────────────────────────────

    [Fact]
    public void Transform_Jwt_ExtractsPayload()
    {
        // header.payload.signature，payload = {"sub":"user-42","role":"admin"}
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"user-42\",\"role\":\"admin\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string jwt = $"eyJhbGciOiJIUzI1NiJ9.{payload}.signature";

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "token", jwt } }
        };

        var rules = new AnalysisRules();
        rules.Fields["token"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "jwt" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "token");
        Assert.Equal("{\"sub\":\"user-42\",\"role\":\"admin\"}", stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
    }

    [Fact]
    public void Transform_Jwt_ExtractsSpecificClaim()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"user-42\",\"role\":\"admin\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string jwt = $"eyJhbGciOiJIUzI1NiJ9.{payload}.sig";

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "token", jwt } }
        };

        var rules = new AnalysisRules();
        rules.Fields["token"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "jwt", Claim = "role" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "token");
        Assert.Equal("admin", stat.ValueCounts.Keys.Single());
    }

    // ──────────────────────────────────────────────
    // AES 解密
    // ──────────────────────────────────────────────

    [Fact]
    public void Transform_Aes_DecryptsValue()
    {
        // 使用固定的 32 字节 key + 16 字节 iv 加密 "sensitive-data"
        byte[] key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        byte[] iv = Encoding.UTF8.GetBytes("abcdefghijklmnop");
        string plainText = "sensitive-data";

        byte[] cipher;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plainText), 0, plainText.Length);
        }

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "secret", Convert.ToBase64String(cipher) } }
        };

        var rules = new AnalysisRules();
        rules.Fields["secret"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "aes",
                Key = Convert.ToBase64String(key),
                Iv = Convert.ToBase64String(iv)
            }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "secret");
        Assert.Equal(plainText, stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
    }

    [Fact]
    public void Transform_Aes_UnifiedFormatField_Works()
    {
        // 与上例相同，但显式声明统一的 format 编码
        byte[] key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        byte[] iv = Encoding.UTF8.GetBytes("abcdefghijklmnop");
        string plainText = "sensitive-data";

        byte[] cipher;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plainText), 0, plainText.Length);
        }

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "secret", Convert.ToBase64String(cipher) } }
        };

        var rules = new AnalysisRules();
        rules.Fields["secret"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "aes",
                Key = Convert.ToBase64String(key),
                Iv = Convert.ToBase64String(iv),
                Format = "base64"
            }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "secret");
        Assert.Equal(plainText, stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
    }

    [Fact]
    public void Transform_Aes_InvalidFormat_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "aes", Key = "aGVsbG8=", Format = "hexx" }
        };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("format", ex.Message);
    }

    // ──────────────────────────────────────────────
    // 转换失败：保留原始值 + 记录失败次数
    // ──────────────────────────────────────────────

    [Fact]
    public void Transform_Failure_KeepsOriginalAndCountsFailure()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "encoded", "!!!not-valid-base64!!!" } },
            new() { { "encoded", Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")) } }
        };

        var rules = new AnalysisRules();
        rules.Fields["encoded"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "encoded");
        Assert.Equal(1, stat.TransformFailureCount);
        // 失败值保留原始值，成功值被转换
        Assert.Contains("!!!not-valid-base64!!!", stat.ValueCounts.Keys);
        Assert.Contains("ok", stat.ValueCounts.Keys);
    }

    [Fact]
    public void Transform_InvalidType_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule { Transform = new FieldTransform { Type = "nope" } };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("不支持", ex.Message);
    }

    [Fact]
    public void Transform_AesMissingKey_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule { Transform = new FieldTransform { Type = "aes" } };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("key", ex.Message);
    }

    [Fact]
    public void TransformFailureCount_OmitsFromJsonWhenZero()
    {
        var stat = new FieldStatistic { FieldName = "A", Count = 1 };
        string json = JsonSerializer.Serialize(stat);
        Assert.DoesNotContain("TransformFailureCount", json);
        Assert.DoesNotContain("TransformType", json);
    }

    [Fact]
    public void TransformFailureCount_IncludesInJsonWhenNonZero()
    {
        var stat = new FieldStatistic { FieldName = "A", Count = 1, TransformFailureCount = 3, TransformType = "jwt" };
        string json = JsonSerializer.Serialize(stat);
        Assert.Contains("TransformFailureCount", json);
        Assert.Contains("\"TransformType\":\"jwt\"", json);
    }

    // ──────────────────────────────────────────────
    // parse: json —— 转换后展开为嵌套字段再统计
    // ──────────────────────────────────────────────

    private static string B64(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParseJson_AfterBase64_ExpandsNestedObject()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "payload", B64("{\"city\":\"上海\",\"age\":30}") } }
        };

        var rules = new AnalysisRules();
        rules.Fields["payload"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64", Parse = "json" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var names = result.FieldStatistics.Select(f => f.FieldName).ToList();
        Assert.Contains("payload.city", names);
        Assert.Contains("payload.age", names);
        Assert.DoesNotContain("payload", names); // 原字段被展开后不再保留

        var city = result.FieldStatistics.First(f => f.FieldName == "payload.city");
        Assert.Equal("上海", city.ValueCounts.Keys.Single());
        var age = result.FieldStatistics.First(f => f.FieldName == "payload.age");
        Assert.Equal("30", age.ValueCounts.Keys.Single());
    }

    [Fact]
    public void ParseJson_AfterJwt_ExpandsPayloadClaims()
    {
        string payload = B64("{\"sub\":\"user-42\",\"role\":\"admin\"}")
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string jwt = $"eyJhbGciOiJIUzI1NiJ9.{payload}.sig";

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "token", jwt } }
        };

        var rules = new AnalysisRules();
        rules.Fields["token"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "jwt", Parse = "json" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var names = result.FieldStatistics.Select(f => f.FieldName).ToList();
        Assert.Contains("token.sub", names);
        Assert.Contains("token.role", names);

        var sub = result.FieldStatistics.First(f => f.FieldName == "token.sub");
        Assert.Equal("user-42", sub.ValueCounts.Keys.Single());
    }

    [Fact]
    public void ParseJson_Array_ExpandsIndexedFields()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "tags", B64("[\"a\",\"b\",\"c\"]") } }
        };

        var rules = new AnalysisRules();
        rules.Fields["tags"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64", Parse = "json" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var names = result.FieldStatistics.Select(f => f.FieldName).ToList();
        Assert.Contains("tags[0]", names);
        Assert.Contains("tags[1]", names);
        Assert.Contains("tags[2]", names);
    }

    [Fact]
    public void ParseJson_InvalidJson_KeepsRawTransformedText()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "payload", B64("not a json object") } }
        };

        var rules = new AnalysisRules();
        rules.Fields["payload"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64", Parse = "json" }
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = Assert.Single(result.FieldStatistics);
        Assert.Equal("payload", stat.FieldName);
        Assert.Equal("not a json object", stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount); // 转换成功，仅解析失败，不计为失败
    }

    [Fact]
    public void ParseJson_WithoutType_ExpandsPlainJsonField()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "info", "{\"level\":2,\"active\":true}" } }
        };

        var rules = new AnalysisRules();
        rules.Fields["info"] = new FieldRule
        {
            Transform = new FieldTransform { Parse = "json" } // 无 type，仅展开
        };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var names = result.FieldStatistics.Select(f => f.FieldName).ToList();
        Assert.Contains("info.level", names);
        Assert.Contains("info.active", names);
    }

    [Fact]
    public void ParseJson_SelectionCanTargetNestedFieldAfterOuterParse()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new()
            {
                {
                    "A",
                    "{\"body\":\"{\\\"mountain\\\":\\\"bear\\\",\\\"mill\\\":\\\"difficulty\\\"}\"}"
                }
            }
        };

        var rules = new AnalysisRules();
        rules.Fields["A"] = new FieldRule
        {
            Transform = new FieldTransform { Parse = "json" }
        };
        rules.SelectedFields = new List<string> { "A.body.mountain" };

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var field = result.FieldStatistics.First(f => f.FieldName == "A.body.mountain");
        Assert.Equal("bear", field.ValueCounts.Keys.Single());
    }

    [Fact]
    public void ParseJson_FilterAppliesToExpandedField()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "payload", B64("{\"city\":\"上海\",\"age\":30}") } },
            new() { { "payload", B64("{\"city\":\"北京\",\"age\":20}") } }
        };

        var rules = new AnalysisRules();
        rules.Fields["payload"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "base64", Parse = "json" }
        };

        var processor = new FieldTransformProcessor(rules, filterExpression: "payload.age >= 25");
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        Assert.Equal(1, result.TotalObjects);
        var city = result.FieldStatistics.First(f => f.FieldName == "payload.city");
        Assert.Equal("上海", city.ValueCounts.Keys.Single());
    }

    [Fact]
    public void ParseJson_InvalidParseMode_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule { Transform = new FieldTransform { Type = "base64", Parse = "yaml" } };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("parse", ex.Message);
    }

    [Fact]
    public void ParseJson_WithoutTypeAndParse_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule { Transform = new FieldTransform { Type = "  " } };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("type", ex.Message);
    }
}
