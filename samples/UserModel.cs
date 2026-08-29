using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Demo;

/// <summary>
/// rules-from-code 示例模型：演示 primaryType / 枚举 / 必填 / 范围 / 模式 / 默认值 的映射。
/// 生成规则文件：objectanalyzer rules-from-code --input samples/UserModel.cs -o rules.json
/// </summary>
public class UserModel
{
    [JsonPropertyName("userName")]
    [Required]
    public string Name { get; set; } = "unknown";

    [Range(0, 150)]
    public int Age { get; set; } = 18;

    [RegularExpression("^1[3-9]\\d{9}$")]
    public string? Phone { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public UserLevel Level { get; set; }

    public string? Remark { get; set; }

    public List<string>? Tags { get; set; }
}

public enum UserLevel
{
    Basic,
    Vip,
    Admin
}
