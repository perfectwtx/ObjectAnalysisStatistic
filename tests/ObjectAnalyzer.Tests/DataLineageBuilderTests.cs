using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

public sealed class DataLineageBuilderTests
{
    [Fact]
    public void Build_CreatesNodesAndEdgesForNestedRelationships()
    {
        var relationships = new[]
        {
            new ObjectRelationship
            {
                Path = "customer",
                Kind = "Object",
                OccurrenceCount = 2,
                TargetFields = new List<string> { "id", "name" },
                TargetFieldCount = 2
            },
            new ObjectRelationship
            {
                Path = "order.shipping",
                Kind = "Object",
                OccurrenceCount = 1,
                TargetFields = new List<string> { "city" },
                TargetFieldCount = 1
            },
            new ObjectRelationship
            {
                Path = "items",
                Kind = "Collection",
                OccurrenceCount = 3,
                TargetFields = new List<string> { "sku", "qty" },
                TargetFieldCount = 2
            }
        };

        var lineage = DataLineageBuilder.Build(relationships);

        Assert.Contains(lineage.Nodes, x => x.Path == "customer" && x.Kind == "Object" && x.Depth == 1);
        Assert.Contains(lineage.Nodes, x => x.Path == "customer.id" && x.Kind == "Field");
        Assert.Contains(lineage.Nodes, x => x.Path == "items.sku" && x.Kind == "Field");
        Assert.Contains(lineage.Edges, x => x.ParentPath == "customer" && x.ChildPath == "customer.id" && x.Kind == "ContainsField");
        Assert.Contains(lineage.Edges, x => x.ParentPath == "items" && x.ChildPath == "items.qty" && x.Kind == "ContainsField");
    }

    [Fact]
    public void Build_CreatesParentEdgeForNestedObject()
    {
        var relationships = new[]
        {
            new ObjectRelationship
            {
                Path = "order",
                Kind = "Object",
                OccurrenceCount = 1,
                TargetFields = new List<string>()
            },
            new ObjectRelationship
            {
                Path = "order.shipping",
                Kind = "Object",
                OccurrenceCount = 1,
                TargetFields = new List<string> { "city" }
            }
        };

        var lineage = DataLineageBuilder.Build(relationships);

        Assert.Contains(lineage.Edges, x =>
            x.ParentPath == "order" &&
            x.ChildPath == "order.shipping" &&
            x.Kind == "Contains");
    }
}
