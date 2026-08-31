using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分析规则配置，包含所有字段规则的集合。
/// </summary>
public class AnalysisRules
{
    /// <summary>
    /// 规则文件反序列化选项：允许 // 注释（参考模板 samples/rules_full_reference.jsonc 可直接使用）
    /// 与大小写不敏感匹配。全项目统一的规则读取入口。
    /// </summary>
    public static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 全局 JSON 展开行为：true 时扁平化嵌套对象，false 时保留对象树。CLI 的 --no-flatten 优先级更高。
    /// <para>新写法：迁移到 <see cref="Runtime"/>.flatten；本顶层字段保留仅用于向后兼容。
    /// 同时存在时以 <see cref="Runtime"/> 为准。</para>
    /// </summary>
    [JsonPropertyName("flatten")]
    public bool? Flatten { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldRule> Fields { get; set; } = new();

    /// <summary>
    /// 仅统计这些字段（逗号分隔）。优先级低于 CLI 的 --fields 选项。
    /// <para>新写法：迁移到 <see cref="Runtime"/>.selectedFields；本顶层字段保留仅用于向后兼容。</para>
    /// </summary>
    [JsonPropertyName("selectedFields")]
    public List<string>? SelectedFields { get; set; }

    /// <summary>
    /// 全局过滤条件表达式（如 "Age &gt; 18 AND Gender in ['男','女']"）。优先级低于 CLI 的 --where 选项。
    /// <para>新写法：迁移到 <see cref="Runtime"/>.filter；本顶层字段保留仅用于向后兼容。</para>
    /// </summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    /// <summary>
    /// 运行时配置（"怎么跑"）。对应 CLI / API 显式参数：CLI / API 显式值 &gt; 本节点 &gt; <see cref="AnalysisOptions"/> 内置默认。
    /// 字段均为可空 —— null 表示"未指定"。
    /// </summary>
    [JsonPropertyName("runtime")]
    public AnalysisRuntime? Runtime { get; set; }

    /// <summary>通用值解析器：按 source + parseType 决定是否解析值，适用于 Excel / JSON / 任意输入来源。</summary>
    [JsonPropertyName("valueParsers")]
    public List<ValueParserConfig>? ValueParsers { get; set; }

    /// <summary>数据集级质量期望（行数下限、最大重复率等）。违规进入 --violations 导出并触发 --fail-on-violations 门禁。</summary>
    [JsonPropertyName("expectations")]
    public AnalysisExpectations? Expectations { get; set; }
}
