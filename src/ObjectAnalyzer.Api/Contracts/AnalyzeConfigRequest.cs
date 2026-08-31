using System.ComponentModel;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>强类型分析配置（POST /api/analyze/config 的 config 字段 JSON）。</summary>
/// <remarks>
/// 与 multipart 逐项表单/查询参数不同，这里把「除数据文件外」的全部选项与规则合并为一个
/// 强类型 JSON 对象：继承 <see cref="AnalyzeRequestOptions"/> 得到全部分析选项，
/// 另含内联强类型规则对象 <see cref="Rules"/>（不是规则文件、不是规则字符串）。
/// </remarks>
public sealed class AnalyzeConfigRequest : AnalyzeRequestOptions
{
    /// <summary>内联强类型规则对象（flatten / valueParsers / fields / filter / expectations）。</summary>
    [Description("内联强类型规则对象（AnalysisRules：flatten/valueParsers/fields/filter/expectations）")]
    public AnalysisRules? Rules { get; init; }
}
