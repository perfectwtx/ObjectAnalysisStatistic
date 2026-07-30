namespace ObjectAnalyzer;

/// <summary>
/// Encapsulates all options for an analysis run, avoiding long parameter lists.
/// </summary>
public sealed class AnalysisOptions
{
    public string InputPath { get; set; } = "";
    public string? OutputPath { get; set; }
    public string? Format { get; set; }
    public bool Flatten { get; set; } = true;
    public int MaxValuesToShow { get; set; } = 5;
    public string? TypeFile { get; set; }
    public string? TypeName { get; set; }
    public string? ExcelJsonColumn { get; set; }
}
