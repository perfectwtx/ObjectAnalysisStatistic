# Core 值模型与字段路径

本次升级统一值身份、字段路径、解析转换、频次和去重编码。计算状态与最终结果分离、任务编排等后续重构不包含在本次修改中。

## 值身份

`ObjectAnalyzer.Data.RecordValue` 是不可变值快照。支持 null、布尔、数字、字符串、日期时间、日期、时间、时长、GUID、二进制、对象和数组。

- 数字 `1`、字符串 `"1"`、null 和字符串 `"null"` 是四种不同值。
- 数值相等的 `1L`、`1.0m`、`1d` 和 JSON `1e0` 具有相同身份。数字通过十进制系数和指数规范化，不先经过 double 舍入。
- decimal、大整数和极大指数可保留原始数值身份。`ToObject()` 优先返回无损的 long 或 decimal；无法无损转换时返回 RecordValue。
- 对象属性顺序不影响身份，属性名区分大小写；数组顺序影响身份。
- DateTimeOffset 按 UTC 规范化；DateTime 保留其时区类别。日期、GUID、二进制与相同显示文本的字符串不同。
- 不支持的 CLR 自定义对象显式报错，应先映射为支持的对象图。嵌套深度上限为 128。

`CanonicalKey` 用于去重和 HLL，具有 `rv1:` 版本前缀。`ToDisplayString()` 只负责显示，不能用作去重、枚举或字典身份。`ToJsonString()` 输出普通数据 JSON，RecordValue 的 JSON converter 则输出带类型的持久化表示。

均值、分位数等现有数值算法仍使用 double。保留值身份的精度不代表所有统计量已升级为精确十进制运算。

## 字段路径

`FieldPath` 由属性段和非负数组索引段组成，区分大小写。文本格式如下：

| 含义 | 编码 |
|---|---|
| user 下的 name | `user.name` |
| 原始属性名 user.name | `["user.name"]` |
| 数组的第一个元素 | `items[0]` |
| 原始属性名 items[0] | `["items[0]"]` |
| 空属性名 | `[""]` |
| 属性名包含逗号 | `["a,b"]` |
| 二维数组 | `matrix[0][1]` |

引号、反斜杠和控制字符采用 JSON 字符串转义。空路径代表根，与空属性名不同。`Parse()` 规范化等价写法，`Parent`、`IsPrefixOf()` 按结构操作，不能使用字符串截断推导父级。

选择列表使用 `FieldPath.ParseList()`，不会拆开带引号属性名中的逗号。过滤表达式也支持带引号的完整路径，如 `["a.b"] == 1`。字段查找和枚举值匹配区分大小写；过滤表达式的 AND/OR/LIKE 等操作符保留原有语义。

## 复用入口

```csharp
using ObjectAnalyzer.Data;
using Engine = ObjectAnalyzer.Analysis.ObjectAnalyzer;

var source = new Dictionary<string, object?>
{
    ["a.b"] = 1,
    ["a"] = new Dictionary<string, object?> { ["b"] = "1" }
};

var record = RecordNormalizer.Normalize(RecordValue.FromObject(source));
var result = Engine.AnalyzeRecords([record]);
```

`RecordNormalizer.Normalize()` 接收原始对象图并创建结构化字段路径。`Flatten()` 可从指定前缀继续展平，保留空对象、空数组和数组的数组。

现有 `Analyze(IEnumerable<Dictionary<string, object?>>)` 保留为适配入口：字典键表示已编码的字段路径，值表示 CLR 数据。需要原始属性名语义时，使用上述 Normalizer 或 `FieldPath.Property(name)`，不要把未转义的点号字段名直接作为路径传入。

JSON、JSONL、YAML 展平、Excel-JSON 和转换后的 JSON 使用共享归一化逻辑。CSV/Excel 表头和数据库列名作为属性段编码；数据库转换保留 decimal、日期、GUID、二进制等类型。Schema、规则生成、字段选择和血缘均消费相同路径语义。

## 频次与输出兼容性

`FieldStatistic.ValueCounts` 现在为 `Dictionary<RecordValue, long>`，查询数字请使用 `RecordValue.FromObject(1)`，字符串可使用 `RecordValue.FromString("1")` 或字符串到 RecordValue 的隐式转换。

截断数量通过 `OtherValueCount` 单独返回。真实数据 `"[Other values]"` 不再承担特殊含义。精确频次与 HLL 使用相同的值域，均包含显式 null。

持久化频次字典的 JSON 键包含版本及类型，例如数值 1 的键为 `rv1:["Number","1"]`，字符串 1 的键为 `rv1:["String","1"]`。导出器和展示层应调用显示方法，不能直接显示 CanonicalKey。

C# Schema 生成根据所有观测值的数字证据选择 long、double、decimal 或 JsonElement，避免用均值推测整数类型。清洗后重名的属性生成唯一标识符，并使用 JsonPropertyName 保留原始名称。

## 迁移

- 旧版无类型的频次 JSON 已丢失类型信息，读取时明确拒绝；请重新分析数据源生成报告。
- 分析 checkpoint 升级为 `object-analyzer.snapshot/v2`。
- Schema 与分布快照升级为 `snapshotVersion: "2.0"`。旧 HLL 的哈希输入语义不同，禁止跨版本比较，需重新生成基线。
- 旧字段名曾存在点号/索引歧义，无法可靠自动恢复。规则中表示原始特殊属性名的键应改为带引号的路径。
- 类型区分和严格枚举匹配会改变部分历史统计及门禁结果，这是本次明确的数据契约变更。
