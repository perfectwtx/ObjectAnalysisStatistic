using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 进度显示：Spectre.Console 进度条 + ETA（默认开启）。
/// </summary>
public class ProgressConfig
{
    /// <summary>是否显示进度条。false 时静默运行（CI / 静默流水线友好）。与 <c>--no-progress</c> 取反对应。</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
