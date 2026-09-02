using System.ComponentModel;
using Microsoft.AspNetCore.Http;

namespace ObjectAnalyzer.Api;

/// <summary>multipart 分析表单（字段注释会进入 OpenAPI / Scalar 文档）。</summary>
/// <remarks>
/// 继承 <see cref="AnalyzeRequestOptions"/> 得到全部分析选项（flatten/csvInferNumbers/fields/filter/sample*/maxParallel + 深度分析开关），
/// 仅追加 multipart 专属字段（数据文件 / 规则文件 / 内联规则 / 未知键开关）。
/// </remarks>
public sealed class AnalyzeForm : AnalyzeRequestOptions
{
    /// <summary>数据文件：JSON / JSONL / CSV / Excel / XML / YAML（可直接上传 gzip/bzip2/zip 压缩包）。</summary>
    [Description("数据文件（JSON/JSONL/CSV/Excel/XML/YAML，支持压缩包）")]
    public IFormFile? File { get; init; }

    /// <summary>规则文件（.json，支持 // 注释；与 rulesJson 二选一）。</summary>
    [Description("规则文件（.json，支持 // 注释）")]
    public IFormFile? Rules { get; init; }

    /// <summary>内联规则 JSON 文本（优先于规则文件）。</summary>
    [Description("内联规则 JSON（优先于规则文件）")]
    public string? RulesJson { get; init; }

    /// <summary>规则里出现后端不认识的键时仍然继续分析（默认 false：直接报 400）。</summary>
    [Description("容忍规则里不认识的键（默认 false，拼错会报错）")]
    public bool? AllowUnknownRules { get; init; }
}
