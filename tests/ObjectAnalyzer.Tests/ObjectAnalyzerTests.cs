namespace ObjectAnalyzer.Tests;

public class ObjectAnalyzerTests
{
    [Fact]
    public void Analyze_EmptyList_ReturnsZeroTotals()
    {
        // Arrange
        var objects = new List<Dictionary<string, object?>>();

        // Act
        var result = Analyze(objects);

        // Assert
        Assert.Equal(0, result.TotalObjects);
        Assert.Equal(0, result.TotalUniqueObjects);
        Assert.Empty(result.DuplicateObjects);
        Assert.Empty(result.FieldStatistics);
    }

    [Fact]
    public void Analyze_SimpleObjects_CalculatesCoverageAndTypesCorrectly()
    {
        // Arrange
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Name", "Alice" }, { "Active", true } },
            new() { { "Id", 2 }, { "Name", "Bob" } }, // Active is missing
            new() { { "Id", 3 }, { "Name", null }, { "Active", false } } // Name is null
        };

        // Act
        var result = Analyze(objects);

        // Assert
        Assert.Equal(3, result.TotalObjects);
        
        // Check "Id" (always present, numeric, no nulls)
        var idStat = result.FieldStatistics.First(f => f.FieldName == "Id");
        Assert.Equal(3, idStat.Count);
        Assert.Equal(1.0, idStat.Coverage);
        Assert.Contains("Number", idStat.TypeCounts.Keys);
        Assert.Equal("0", idStat.DefaultValueRepresentation);

        // Check "Name" (present in all, but has a null value)
        var nameStat = result.FieldStatistics.First(f => f.FieldName == "Name");
        Assert.Equal(3, nameStat.Count);
        Assert.Equal(1.0, nameStat.Coverage);
        Assert.Contains("String", nameStat.TypeCounts.Keys);
        Assert.Contains("Null", nameStat.TypeCounts.Keys);
        Assert.Equal("null", nameStat.DefaultValueRepresentation);

        // Check "Active" (missing in one object)
        var activeStat = result.FieldStatistics.First(f => f.FieldName == "Active");
        Assert.Equal(2, activeStat.Count);
        Assert.Equal(2.0 / 3.0, activeStat.Coverage);
        Assert.Contains("Boolean", activeStat.TypeCounts.Keys);
        Assert.Equal("false", activeStat.DefaultValueRepresentation); // Missing implies null
    }

    [Fact]
    public void Analyze_ValueTypesWithoutNull_InfersCorrectDefaults()
    {
        // Arrange
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Score", 95 }, { "IsAdmin", true } },
            new() { { "Score", 80 }, { "IsAdmin", false } }
        };

        // Act
        var result = Analyze(objects);

        // Assert
        var scoreStat = result.FieldStatistics.First(f => f.FieldName == "Score");
        Assert.Equal("0", scoreStat.DefaultValueRepresentation);

        var adminStat = result.FieldStatistics.First(f => f.FieldName == "IsAdmin");
        Assert.Equal("false", adminStat.DefaultValueRepresentation);
    }

    [Fact]
    public void Analyze_DuplicateObjects_IdentifiesDuplicatesCorrectly()
    {
        // Arrange
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Val", "A" } },
            new() { { "Id", 2 }, { "Val", "B" } },
            new() { { "Id", 1 }, { "Val", "A" } }, // Duplicate 1
            new() { { "Id", 1 }, { "Val", "A" } }  // Duplicate 2
        };

        // Act
        var result = Analyze(objects);

        // Assert
        Assert.Equal(4, result.TotalObjects);
        Assert.Equal(2, result.TotalUniqueObjects); // {"Id": 1,"Val":"A"} and {"Id": 2,"Val":"B"}
        Assert.Single(result.DuplicateObjects);
        
        var duplicate = result.DuplicateObjects.First();
        Assert.Equal(3, duplicate.Count);
        Assert.Contains("\"Id\": 1", duplicate.ObjectJson);
        Assert.Contains("\"Val\": \"A\"", duplicate.ObjectJson);
    }
}
