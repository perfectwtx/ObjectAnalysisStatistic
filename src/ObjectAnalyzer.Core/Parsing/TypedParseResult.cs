using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// 强类型反序列化的结果：对象列表 + 属性读取错误列表（分离，避免错误文本污染统计数据）。
/// </summary>
public sealed class TypedParseResult
{
    public List<Dictionary<string, object?>> Objects { get; } = new();
    public List<string> Errors { get; } = new();
}
