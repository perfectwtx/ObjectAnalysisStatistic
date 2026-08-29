using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>覆盖率变化（baseline - current 视为变化量；正数表示覆盖率下降）。</summary>
public sealed class DistributionFieldChange
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("baselineCoverage")] public double BaselineCoverage { get; set; }
    [JsonPropertyName("currentCoverage")] public double CurrentCoverage { get; set; }
    [JsonPropertyName("delta")] public double Delta { get; set; }
}
