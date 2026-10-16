using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分析规则配置，包含所有字段规则的集合。
/// </summary>
public class AnalysisRules
{
    /// <summary>
    /// 规则文件反序列化选项：允许 JSONC 注释与尾随逗号（参考模板 samples/rules_full_reference.jsonc 可直接使用）
    /// 与大小写不敏感匹配。全项目统一的规则读取入口。
    /// </summary>
    public static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldRule> Fields { get; set; } = new();

    /// <summary>
    /// 运行时配置（"怎么跑"）。原始 JSON 节点，<see cref="RulesToOptionsConverter"/> 一次性解析并写入
    /// <see cref="AnalysisOptions"/> 对应子组。不在 DTO 中展开为强类型类，避免多层转换。
    /// </summary>
    [JsonPropertyName("runtime")]
    public JsonObject? Runtime { get; set; }

    /// <summary>通用值解析器：按 source + parseType 决定是否解析值，适用于 Excel / JSON / 任意输入来源。</summary>
    [JsonPropertyName("valueParsers")]
    public List<ValueParserConfig>? ValueParsers { get; set; }

    /// <summary>数据集级质量期望（行数下限、最大重复率等）。违规进入 --violations 导出并触发 --fail-on-violations 门禁。</summary>
    [JsonPropertyName("expectations")]
    public AnalysisExpectations? Expectations { get; set; }
}
