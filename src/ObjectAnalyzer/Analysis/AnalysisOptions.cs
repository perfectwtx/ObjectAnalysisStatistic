namespace ObjectAnalyzer.Analysis;

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
    public string? RulesFile { get; set; }
    public string? ViolationsPath { get; set; }
    public string? QualityPath { get; set; }
    public bool FailOnViolations { get; set; }
    public int MaxDegreeOfParallelism { get; set; } = -1; // -1 = 使用默认（所有核心）
    public string? ExportSchema { get; set; } // "csharp", "typescript", "json-schema"
    public string? SchemaOutputPath { get; set; }

    /// <summary>仅统计这些字段（逗号分隔，来自 --fields）。</summary>
    public string? Fields { get; set; }

    /// <summary>按字段值过滤对象后再统计（--where，如 "Age &gt; 18 AND Status in ['A','B']"）。</summary>
    public string? Filter { get; set; }

    /// <summary>蓄水池随机抽样容量（--sample-reservoir，>0 启用）。</summary>
    public int SampleReservoir { get; set; }

    /// <summary>等距系统抽样步长（--sample-step，>1 启用）。</summary>
    public int SampleStep { get; set; }
}
