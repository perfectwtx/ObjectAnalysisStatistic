# Schema 推断与导出模型

更新日期：2026-10-18。对应 Core 优化第 8 项。

## 架构与扩展入口

原有导出树同时保存多种目标类型。现在分为三个步骤：

1. `SchemaInference.Infer(AnalysisResult, SchemaInferenceOptions?)` 生成不可变的 `SchemaDocument`，版本为 `schema-model/v1`。
2. `SchemaNamePlan` 根据目标规则独立分配类型名、属性名与来源路径，统一处理特殊字符、关键字和冲突。
3. 各 `ISchemaBackend` 消费同一类型图，返回内容与诊断。新增格式可以直接实现接口，无需修改推断器。

模型位于 `Analysis/Schema/Inference`；命名和六类后端位于 `Reporting/Schema`。图中不保存目标语言类型字符串，也不持有可变的 FieldStatistic。

`SchemaExporter.Generate(document, format, options)` 是格式选择门面。原有 GenerateCSharp、GenerateTypeScript、GenerateJsonSchema、GenerateOpenApiJson/Yaml、GenerateProtobuf、GenerateSql 和 Export 入口保留。

```csharp
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Reporting;

var document = SchemaInference.Infer(result);
var exported = SchemaExporter.Generate(document, "typescript",
    new SchemaExportOptions { RootName = "Order" });
File.WriteAllText("order.ts", exported.Content);
foreach (var diagnostic in exported.Diagnostics)
    Console.WriteLine($"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}");
```

## 类型与证据语义

- Kinds 保留全部观察到的类型，异构数据形成联合类型，不使用最后一次观察或仅使用主要类型。
- SchemaPresence 表示 Required、Optional 或 Unknown，与是否允许 null 独立：必填字段可以为 null，可选字段也可以只接受非 null 值。
- 对象属性和数组元素递归建模；不同数组索引归并为同一元素类型，对象元素的属性出现证据共同参与推断。
- 空数组通过 EmptyArrayObserved 表示，无证据的元素保留未知；非空数组证据不会被空数组覆盖。
- HasUnknownValues、ShapeIncomplete 表达未知值及不完整结构。截断或脱敏后的复杂样本不能用于猜测原始内部类型。
- 聚合统计无法恢复所有跨字段共现关系。推断器使用出现次数上下界证明必填或可选，无法证明时返回 Unknown 与 presence-unknown 诊断。
- SemanticTypes 只是分析器提示。完整、未脱敏且全部字符串证据通过验证后才进入 VerifiedSemanticTypes，供 UUID、日期等映射使用。80% 语义识别置信度不足以缩窄整个字段类型。
- Numeric 保留整数性、Int64/decimal/double 可表示性、精确数值文本范围、范围完整性、整数位数和小数位数。比较支持科学计数法，不通过平均数判断整数。
- 默认最多 20,000 个结构节点、深度 128，超限明确拒绝。SchemaInferenceOptions 可调整推断限制，命名阶段也有结构保护。

稳定排序、结构路径及确定性命名保证记录、属性和统计项顺序变化不影响推断及导出。导出无法恢复此前已丢弃的信息。

## 各目标的表达与降级

| 目标 | 行为与边界 |
|---|---|
| C# | record、JsonPropertyName、必填标记、扩展字段字典；混合或未知结构、超出原生数字能力的值使用 JsonElement。缺失映射 nullable 时报告语义损失。 |
| TypeScript | 联合类型、独立的可选属性与 null、递归数组及开放对象；未知使用 unknown，超出 JS 数字安全表达范围时告警。 |
| JSON Schema | draft 2020-12，type/anyOf、required、递归 items、精确枚举；观察范围通过扩展属性表达。 |
| OpenAPI | 3.1.0，采用 JSON Schema 2020-12 语义；组件名称全局去重，JSON 与 YAML 共用文档模型。 |
| Protobuf | proto3 标量、对象和 repeated；异构值或嵌套数组等使用 JSON 文本并报告诊断。任意精度数字可回退精确文本。 |
| SQL | PostgreSQL、MySQL、SQLite、SQL Server；依据数字域选择整数、浮点、decimal 或文本，复杂结构使用 JSON 存储，标识符冲突及缺失/null 合并显式告警。 |

枚举在可表达的格式中生成约束，否则报告未强制枚举的诊断。原生本地 DateTime、TimeOnly、TimeSpan 不会直接宣称符合 RFC 3339 格式。

默认允许降级，诊断通过 SchemaExportResult.Diagnostics 和生成文件中的注释或 JSON 扩展返回。设置 `Strict = true` 后，任何诊断都会抛出 SchemaExportException，其 Diagnostics 包含原因。严格模式拒绝当前后端的全部已报告限制，不仅是语法错误。

Protobuf 字段编号由原始字段名确定性哈希分配并避开保留区间。已有发布协议仍应人工固定编号；哈希碰撞处理不能替代协议演进管理。JSON 文本回退需要调用方按诊断进行序列化适配。

## 兼容与迁移

静态导出方法保留，但输出有意调整：OpenAPI 升级至 3.1.0；未知类型不再猜成字符串；整数、缺失、null、特殊名称及枚举顺序统一；部分输出新增诊断和更保守的类型。消费生成代码或依赖文本快照的调用方应重新生成并检查差异，OpenAPI 消费方需支持 3.1。

类型图用于推断和导出，不作为分析续算状态。既有 analysis-state/v3、检查点和扁平 SchemaSnapshot v2 持久化协议不变，历史结果存储与脱敏投影保持现有边界。

## 验证与复现

新增 SchemaModelTests 共 23 项，覆盖缺失/null、空与异构/嵌套数组、索引归并、顺序不变性、数字范围和精度、脱敏/截断、特殊名称和冲突、枚举、严格模式及资源限制。生成 C# 使用 Roslyn 编译，包含 JSON 反序列化与往返；OpenAPI 检查全部组件引用并解析 YAML；SQLite DDL 在内存数据库执行。

完整构建零警告、零错误。Core 792/794，失败仍为此前已有的 SqliteQualityBaselineStoreTests.SaveGetAndDelete_Works 与 Save_ExistingSource_UpdatesBaseline（临时数据库文件锁）；API 132/132，Web 74/74。

外部验证依赖放在忽略的 artifacts 目录，不修改项目依赖。在仓库根目录执行：

```powershell
npm install --prefix artifacts/schema-validation-tools --ignore-scripts --no-audit --no-fund --package-lock=false typescript protobufjs ajv ajv-formats
$env:OBJECT_ANALYZER_SCHEMA_EXPORT_DIR = Join-Path (Get-Location) 'artifacts/schema-fixtures'
dotnet test tests/ObjectAnalyzer.Tests --filter FullyQualifiedName~ExternalCompilerFixture
node scripts/validate-schema-exports.mjs
```

本次使用 TypeScript 7.0.2、protobufjs 8.8.1、Ajv 8.20.0 和 ajv-formats 3.0.1。TypeScript 严格编译零诊断；Protobuf 解析及全部类型引用解析成功；JSON Schema 2020-12 通过两条原始复杂样本验证。Protobuf 验证采用 protobufjs，尚未运行原生 protoc；SQL 执行覆盖 SQLite，其他方言由生成断言验证。
