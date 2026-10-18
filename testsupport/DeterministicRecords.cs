using System.Globalization;

namespace ObjectAnalyzer.TestData;

/// <summary>Versioned deterministic workload shared by contracts and benchmarks. No clocks or randomized hashing.</summary>
public static class DeterministicRecords
{
    public const string Version = "mixed-records/v1";
    public static Dictionary<string, object?>[] Create(int count, int cardinality)
        => Enumerable.Range(0, count).Select(index => new Dictionary<string, object?>
        {
            ["id"] = index % cardinality,
            ["amount"] = (index * 17 % 1000 - 500) / 4d,
            ["group"] = "group-" + (index % cardinality).ToString(CultureInfo.InvariantCulture),
            ["nullable"] = index % 5 == 0 ? null : "present",
            ["enabled"] = index % 2 == 0,
            ["nested"] = new Dictionary<string, object?> { ["a.b"] = index % 7 },
            ["array"] = new object?[] { index % 3, "item", null }
        }).ToArray();
}
