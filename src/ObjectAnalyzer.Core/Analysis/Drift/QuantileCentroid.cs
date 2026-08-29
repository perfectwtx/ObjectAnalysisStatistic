using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>t-digest 单个质心（均值 + 权重）。</summary>
public sealed class QuantileCentroid
{
    [JsonPropertyName("mean")] public double Mean { get; set; }
    [JsonPropertyName("weight")] public long Weight { get; set; }
}
