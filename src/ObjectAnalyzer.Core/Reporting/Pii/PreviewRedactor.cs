namespace ObjectAnalyzer.Reporting;

/// <summary>Preview adapter to the same structural value projector used by reports and duplicates.</summary>
public static class PreviewRedactor
{
    public static IReadOnlyList<Dictionary<string, object?>> Redact(
        IReadOnlyList<Dictionary<string, object?>> preview, PiiPolicy policy)
        => ResultProjection.Preview(preview, policy);
}
