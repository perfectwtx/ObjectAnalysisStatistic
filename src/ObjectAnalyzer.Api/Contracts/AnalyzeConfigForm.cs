using System.ComponentModel;
using Microsoft.AspNetCore.Http;

namespace ObjectAnalyzer.Api;

/// <summary>/api/analyze/config 的 multipart 表单：数据文件 + 强类型配置 JSON。</summary>
public sealed class AnalyzeConfigForm
{
    /// <summary>数据文件：JSON / JSONL / CSV / Excel / XML / YAML（可直接上传 gzip/bzip2/zip 压缩包）。</summary>
    [Description("数据文件（JSON/JSONL/CSV/Excel/XML/YAML，支持压缩包）")]
    public IFormFile? File { get; init; }

    /// <summary>强类型分析配置 JSON：选项 + 内联 rules 规则对象 + 各分析开关；不含数据，数据走 file。</summary>
    [Description("强类型分析配置 JSON（选项 + rules 规则对象 + P4 开关），不含数据")]
    public string? Config { get; init; }
}
