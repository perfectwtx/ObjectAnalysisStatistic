using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Tests;

public sealed class GeneratedValueContractTests
{
    private static readonly string[] Names = ["", "a.b", "[0]", "quote\"", "slash\\", "comma,name", "换行\n", "空格 ", "😀", "0", "a", "é"];

    [Theory]
    [InlineData(17)]
    [InlineData(932)]
    [InlineData(20261018)]
    public void GeneratedPaths_PreserveSegmentsParentsPrefixesAndDictionaryKeys(int seed)
    {
        var random = new Random(seed);
        for (int example = 0; example < 160; example++)
        {
            var path = FieldPath.Root;
            for (int depth = 0, length = random.Next(1, 40); depth < length; depth++)
                path = random.Next(3) == 0 ? path.AppendIndex(random.Next(1000)) : path.AppendProperty(Names[random.Next(Names.Length)]);
            Assert.Equal(path.Segments.ToArray(), FieldPath.Parse(path.ToString()).Segments.ToArray());
            Assert.Equal(path, JsonSerializer.Deserialize<FieldPath>(JsonSerializer.Serialize(path)));
            Assert.True(path.Parent.IsPrefixOf(path));
            Assert.Equal(path.Segments.Length - 1, path.Parent.Segments.Length);
            var map = new Dictionary<FieldPath, int> { [path] = example };
            var restored = JsonSerializer.Deserialize<Dictionary<FieldPath, int>>(JsonSerializer.Serialize(map))!;
            Assert.Equal(example, restored[path]);
            Assert.NotEqual(FieldPath.Property("a.b"), FieldPath.Parse("a.b"));
        }
    }

    [Theory]
    [InlineData(17)]
    [InlineData(932)]
    [InlineData(20261018)]
    public void GeneratedTypedValues_PreserveIdentityAndFrequencyKeys(int seed)
    {
        var random = new Random(seed);
        for (int example = 0; example < 160; example++)
        {
            var value = RecordValue.FromObject(Value(random, 0));
            var restored = JsonSerializer.Deserialize<RecordValue>(JsonSerializer.Serialize(value))!;
            Assert.Equal(value, restored);
            Assert.Equal(value.GetHashCode(), restored.GetHashCode());
            Assert.Equal(value.CanonicalKey, restored.CanonicalKey);
            var counts = new Dictionary<RecordValue, long> { [value] = example + 1 };
            Assert.Equal(example + 1, JsonSerializer.Deserialize<Dictionary<RecordValue, long>>(JsonSerializer.Serialize(counts))![value]);
        }
    }

    [Theory]
    [InlineData("9007199254740993", "9007199254740993000e-3")]
    [InlineData("1e1000", "100e998")]
    [InlineData("-0", "0.000e999")]
    [InlineData("0.00000000000000000000000000001", "1e-29")]
    public void NumberCanonicalization_IsExactAcrossEquivalentSpellings(string first, string second)
    {
        var a = RecordValue.FromJsonElement(JsonSerializer.Deserialize<JsonElement>(first));
        var b = RecordValue.FromJsonElement(JsonSerializer.Deserialize<JsonElement>(second));
        Assert.Equal(a, b);
        Assert.NotEqual(a, RecordValue.FromString(first));
        Assert.Equal(a, JsonSerializer.Deserialize<RecordValue>(JsonSerializer.Serialize(a)));
    }

    [Fact]
    public void DeepValuesAndObjectOrdering_HaveStableTypedIdentity()
    {
        RecordValue value = RecordValue.FromObject(new BigInteger(long.MaxValue) + 1);
        for (int depth = 0; depth < 60; depth++) value = RecordValue.FromObject(new Dictionary<string, object?> { ["a.b"] = value });
        var options = new JsonSerializerOptions { MaxDepth = 512 };
        Assert.Equal(value, JsonSerializer.Deserialize<RecordValue>(JsonSerializer.Serialize(value, options), options));
        Assert.Equal(RecordValue.FromObject(new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2 }),
            RecordValue.FromObject(new Dictionary<string, object?> { ["a"] = 2, ["b"] = 1 }));
    }

    private static object? Value(Random random, int depth)
    {
        int kind = random.Next(depth < 3 ? 9 : 6);
        return kind switch
        {
            0 => null, 1 => random.Next(2) == 0, 2 => random.NextInt64(),
            3 => random.Next(-100000, 100000) / 1000m, 4 => Names[random.Next(Names.Length)],
            5 => new BigInteger(ulong.MaxValue) + random.Next(1000),
            6 => Enumerable.Range(0, random.Next(5)).Select(_ => Value(random, depth + 1)).ToArray(),
            _ => Names.OrderBy(_ => random.Next()).Take(random.Next(5))
                .ToDictionary(name => name, _ => Value(random, depth + 1), StringComparer.Ordinal)
        };
    }
}
