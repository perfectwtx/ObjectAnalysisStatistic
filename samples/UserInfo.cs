using System;
using System.Collections.Generic;

/// <summary>
/// 示例类型定义，对应 samples/test_data.json 中的对象结构。
/// 用于演示 --type / --type-name 强类型反序列化功能。
/// </summary>
public class UserInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Age { get; set; }
    public string Gender { get; set; } = "";
    public AddressInfo? Address { get; set; }
    public List<string>? Tags { get; set; }
    public Dictionary<string, string>? Skills { get; set; }
}

public class AddressInfo
{
    public string City { get; set; } = "";
    public string? District { get; set; }
}
