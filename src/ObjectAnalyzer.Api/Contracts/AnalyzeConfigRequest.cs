using System.ComponentModel;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>强类型分析请求（POST /api/analyze/config 的 JSON 请求体）。</summary>
/// <remarks>
/// 请求体为纯 JSON：数据文本 <see cref="DataText"/> 内嵌，其余全部选项与规则强类型——
/// 继承 <see cref="AnalyzeRequestOptions"/> 得到全部分析选项（flatten/csvInferNumbers/fields/filter/sample*/maxParallel + P4 开关），
/// 另含内联强类型规则对象 <see cref="Rules"/>（不是规则文件、不是规则字符串）。
/// OpenAPI / Scalar 能看到每个配置字段及其规则子结构。
/// </remarks>
public sealed class AnalyzeConfigRequest : AnalyzeRequestOptions
{
    /// <summary>数据文本：JSON / JSONL / CSV / XML / YAML，随请求体内嵌（唯一非强类型的载体）。</summary>
    [Description("数据文本（JSON/JSONL/CSV/XML/YAML），随请求体内嵌")]
    public string DataText { get; init; } = "";

    /// <summary>数据格式：json / jsonl / csv / excel / xml / yaml；留空按内容自动检测。</summary>
    [Description("数据格式（json/jsonl/csv/excel/xml/yaml），留空自动检测")]
    public string? Format { get; init; }

    /// <summary>内联强类型规则对象（flatten / valueParsers / fields / filter / expectations）。</summary>
    [Description("内联强类型规则对象（AnalysisRules：flatten/valueParsers/fields/filter/expectations）")]
    public AnalysisRules? Rules { get; init; }
}
