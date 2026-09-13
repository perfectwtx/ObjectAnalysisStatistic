using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

public sealed class ObjectRelationshipAnalyzerTests
{
    [Fact]
    public void Track_DiscoversNestedObjectAndCollectionRelationships()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["id"] = 1,
                ["customer"] = new Dictionary<string, object?>
                {
                    ["id"] = 10,
                    ["name"] = "Alice"
                },
                ["items"] = new List<Dictionary<string, object?>>
                {
                    new() { ["sku"] = "A", ["qty"] = 1 },
                    new() { ["sku"] = "B", ["qty"] = 2 }
                }
            },
            new()
            {
                ["id"] = 2,
                ["customer"] = new Dictionary<string, object?>
                {
                    ["id"] = 11,
                    ["name"] = "Bob"
                },
                ["items"] = new List<Dictionary<string, object?>>
                {
                    new() { ["sku"] = "C", ["qty"] = 3 }
                }
            }
        };

        IReadOnlyList<ObjectRelationship>? relationships = null;
        var consumed = ObjectRelationshipAnalyzer.Track(objects, value => relationships = value).ToList();

        Assert.Equal(2, consumed.Count);
        Assert.NotNull(relationships);

        var customer = Assert.Single(relationships!, x => x.Path == "customer" && x.Kind == "Object");
        Assert.Equal(2, customer.OccurrenceCount);
        Assert.Equal(2, customer.TargetFieldCount);
        Assert.Contains("id", customer.TargetFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("name", customer.TargetFields, StringComparer.OrdinalIgnoreCase);

        var items = Assert.Single(relationships!, x => x.Path == "items" && x.Kind == "Collection");
        Assert.Equal(3, items.OccurrenceCount);
        Assert.Equal(2, items.TargetFieldCount);
        Assert.Contains("sku", items.TargetFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("qty", items.TargetFields, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Track_DiscoversNestedRelationshipsRecursively()
    {
        var objects = new[]
        {
            new Dictionary<string, object?>
            {
                ["order"] = new Dictionary<string, object?>
                {
                    ["shipping"] = new Dictionary<string, object?>
                    {
                        ["city"] = "Tokyo"
                    }
                }
            }
        };

        IReadOnlyList<ObjectRelationship>? relationships = null;
        _ = ObjectRelationshipAnalyzer.Track(objects, value => relationships = value).ToList();

        Assert.Contains(relationships!, x => x.Path == "order" && x.Kind == "Object");
        Assert.Contains(relationships!, x => x.Path == "order.shipping" && x.Kind == "Object");
    }
}
