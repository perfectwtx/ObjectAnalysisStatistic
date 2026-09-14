using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

public sealed class ImpactAnalysisBuilderTests
{
    [Fact]
    public void Build_PropagatesBreakingChangeToDescendantsAndParents()
    {
        var diff = new SchemaDiff
        {
            RemovedFields =
            [
                new SchemaSnapshotField { Path = "order.customer.id", PrimaryType = "Number" }
            ]
        };

        var lineage = new DataLineage(
            [
                new DataLineageNode("order", "Object", 1, 10, ["customer"]),
                new DataLineageNode("order.customer", "Object", 2, 10, ["id", "name"]),
                new DataLineageNode("order.customer.id", "Field", 3, 10, [])
            ],
            [
                new DataLineageEdge("order", "order.customer", "Contains"),
                new DataLineageEdge("order.customer", "order.customer.id", "ContainsField")
            ]);

        var result = ImpactAnalysisBuilder.Build(diff, lineage);

        Assert.Equal(ImpactLevel.Critical, result.HighestLevel);
        Assert.Equal(1, result.BreakingChangeCount);
        Assert.Contains(result.Nodes, x => x.Path == "order.customer.id" && x.Level == ImpactLevel.Critical);
        Assert.Contains(result.Nodes, x => x.Path == "order.customer" && x.Type == ImpactType.ParentObject);
        Assert.Contains(result.Nodes, x => x.Path == "order" && x.Type == ImpactType.ParentObject);
    }

    [Fact]
    public void Build_ClassifiesNonBreakingChangeAsMedium()
    {
        var diff = new SchemaDiff
        {
            ChangedFields =
            [
                new SchemaFieldChange
                {
                    Path = "customer.name",
                    Breaking = false,
                    Changes = [new SchemaFieldChangeItem { Property = "semanticType", Description = "changed" }]
                }
            ]
        };

        var result = ImpactAnalysisBuilder.Build(diff, null);

        var node = Assert.Single(result.Nodes);
        Assert.Equal("customer.name", node.Path);
        Assert.Equal(ImpactLevel.Medium, node.Level);
        Assert.Equal(ImpactType.SchemaChange, node.Type);
        Assert.Equal(ImpactLevel.Medium, result.HighestLevel);
    }
}
