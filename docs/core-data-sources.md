# 数据源与解析器契约

第五阶段建立统一输入边界：数据源负责打开与释放资源，解析器负责字节到记录的转换，执行管线负责投影、转换、过滤、采样和统计。

## 公开模型

| 模型 | 职责 |
|---|---|
| IAnalysisDataSource | SourceDescriptor + Read/ReadAsync，返回类型化 SourceRecord |
| IRecordParser | 支持的格式、扩展名、能力声明及同步/异步解析；输入为已解压的流 |
| RecordParserRegistry | 不可变解析器与检测器集合；注册元数据在加入时复制，无进程级可变列表 |
| SourceRecord | 不可变 FieldPath → RecordValue 字段集合，携带 SourceLocation |
| SourceReadOptions | 展平、CSV 类型推断、valueParsers、源游标、根投影、错误策略与诊断回调 |
| SourceCapabilities | 流式、重读、位置恢复、原生异步、根投影、安全错误跳过及可寻址要求 |

SourceRecord 按规范字段路径有序保存字段，保证预览与处理顺序稳定。值与字段路径在解析边界归一化，独立于原始 JsonDocument、读取器和数据库连接的生命周期。数值精度、null、二进制及日期类型沿用第一阶段的值身份。

JSON、JSONL、数据库和调用方提供的记录直接生成值模型。XML、YAML、Excel/CSV 使用第三方库的边界适配器。现有统计管线仍有字符串键字典桥接，但字典保存 RecordValue 实例，统计时不会重新解析这些值；转换、过滤、JSONPath 和对外预览在需要时适配旧对象接口。关系分析已直接消费类型化嵌套值。

## 使用方式

默认文件与数据库选项仍可直接传入 AnalysisExecutor。指定 Source 后，执行器使用该源，选项中不需要填写路径或连接字符串：

~~~csharp
var context = new AnalysisRunContext
{
    Source = DataSources.Stream(
        () => File.OpenRead(path), "jsonl", name: "import"),
    EventSink = HandleEvent
};
var output = new AnalysisExecutor().Execute(options, rules, context, cancellationToken);
~~~

~~~csharp
var source = DataSources.Records(records); // IEnumerable<RecordValue>，每项必须是对象
var context = new AnalysisRunContext { Source = source };
var output = new AnalysisExecutor().Execute(options, rules, context);

var asynchronous = DataSources.AsyncRecords(asyncRecords); // IAsyncEnumerable<RecordValue>
var asyncContext = new AnalysisRunContext { Source = asynchronous };
var asyncOutput = await new AnalysisExecutor().ExecuteAsync(
    options, rules, asyncContext, cancellationToken);
~~~

DataSources.Database(connectionString, provider, query) 通过同一接口读取。独立使用源时，可调用 Read/ReadAsync；也可通过 AnalysisAccumulator.ObserveRecords() 累计其类型化记录。

数据源本身不采样。所有格式使用执行管线中转换、过滤后的同一个采样阶段；SourceReadOptions 不提供采样选项。

## 能力矩阵

| 输入 | 读取方式 | 原生异步 API | 位置恢复 | 根投影 | 遇错跳过 |
|---|---|---|---|---|---|
| JSON | 整份文档缓冲 | 是 | 否 | 是 | 否 |
| JSONL / NDJSON | 逐物理行 | 是 | 未压缩文件或可重读流工厂支持物理行恢复 | 否 | 是 |
| CSV | 逐行，支持前向流 | 否，使用同步库适配 | 否 | 否 | 否 |
| Excel / excel-json | 逐行消费，需要可寻址流；前向输入会缓冲 | 否 | 否 | 否 | 否 |
| XML / YAML | 整份文档缓冲 | 否 | 否 | 否 | 否 |
| 数据库 | DbDataReader 逐行 | 是，实际非阻塞能力由驱动决定 | 否 | 否 | 否 |
| 同步记录序列 | 按调用方枚举 | 否 | 否 | 否 | 否 |
| 异步记录序列 | 按调用方异步枚举 | 是 | 否 | 否 | 否 |

Streaming 表示源可逐条交付且不必先缓冲全部内容，不承诺整个分析的内存固定。Excel 的可寻址需求、JSON/XML/YAML 的文档缓冲、模糊聚类及计算状态仍受各自实现影响；整体预算在第六阶段继续处理。

GetCapabilities(format) 允许一个解析器按格式声明不同能力。已注册元数据会被复制；工厂与检测器自身须满足线程安全契约。Replayable 只表示可以重新打开或重新枚举，不能保证数据库查询结果、文件内容或调用方序列在两次读取间不变。

对不支持的续跑或安全错误跳过，读取前抛出 NotSupportedException。为兼容原有管线，非 JSON 源上的 root-path 仍会产生结构化警告并忽略；独立源读取则严格检查 RootProjection 能力。

## 格式扩展与隔离

~~~csharp
var parsers = RecordParserRegistry.Default
    .With(new MyRecordParser())
    .WithDetector(path => path.EndsWith(".custom") ? "my-format" : null);
var context = new AnalysisRunContext { Parsers = parsers };
var output = new AnalysisExecutor().Execute(options, rules, context);
~~~

MyRecordParser 实现 IRecordParser，通过 Formats、Extensions、Capabilities/GetCapabilities() 声明格式和能力，通过 Parse()/ParseAsync() 产出 SourceRecord。解析器应根据 options.Flatten 统一路径语义，保持输出脱离临时缓冲区，并为可定位的错误提供 SourceReadException。

新格式不需要改动 FileParsers 或分析主引擎。默认集合无法识别未注册的格式，修改一个运行的注册集合不会影响其他运行。自定义检测器优先于扩展名探测。

文件源保留 gzip/bzip2/zip 自动解压和确定性 zip 条目选择。流源接收已解压字节，并要求显式格式；调用方可组合自己的网络流、解压流等适配器。

## 位置、诊断和错误策略

SourceLocation 提供源名、记录序号、物理行、表格行及可选列/工作表字段。JSONL 的 Line 为从 1 开始的原始物理行，包含空行、跳过行和坏行；StartPosition 表示已消费的物理行数。Record 为本次读取的记录序号。CSV/Excel 的 Row 包含表头行，数据库 Row 为查询结果序号；第三方库没有提供的工作表名称保持未知。

默认 MalformedRecordPolicy.Stop：遇错中止，SourceReadException 携带位置和原始异常。JSONL 可配置安全跳过：

~~~csharp
options.Parse.MalformedRecords = MalformedRecordPolicy.Skip;
options.Parse.InputDiagnosticLimit = 20;
~~~

直接读取源时，在 SourceReadOptions 中设置同名策略、DiagnosticLimit 和 DiagnosticSink。Skip 只跳过独立 JSONL 记录的解析或值归一化错误；它不会吞掉 IO、取消、规则执行或计算异常。其他内置格式不具备可靠的错误后重新同步协议，明确拒绝 Skip。

逐条错误诊断最多保留/发送配置数量，完整跳过数另行累计；EOF 时发送 input.skipped-summary。管线上下文的 SkippedInputRecords 始终反映本次读取已跳过的数量，LastSourcePosition 为本次消费的位置增量。输入诊断通过 AnalysisEvent 传播，携带位置与次数，不携带坏行原文。当前 CLI/API/Web 的默认策略仍为 Stop；错误策略可通过 Core 的类型化选项配置，尚未增加新的宿主界面开关。

## 所有权、取消和异步执行

DataSources.File 每次枚举打开自己的输入租约，读取本体保持惰性；格式检测可能短暂打开文件或 zip 中央目录。正常结束、提前停止、解析失败及取消都会沿枚举器释放链关闭输入与解压资源。

DataSources.Stream(stream, ...) 默认为单次消费并拥有流。leaveOpen: true 将所有权保留给调用方，解析器关闭其读取器也不会关闭外部流。工厂重载默认可重读，工厂必须每次返回新的流；设 replayable: false 可明确限制为单次消费。记录序列默认同样为单次消费，除非调用方明确声明可重读。

ReadAsync 使用原生异步 JSON/JSONL、数据库或调用方异步序列；同步库使用同步适配，NativeAsync 明确为 false。ExecuteAsync 在工作线程运行计算，通过容量 128 的有界队列连接异步输入和同步统计，和既有 10,000 条计算分块共同构成读入缓冲；它不会先收集全部异步记录。消费者失败也会取消生产者并等待其枚举器释放。

异步输入支持取消，数据库读取传入 token 并尝试取消命令。可寻址缓冲和解压读取同样检查取消。一次同步第三方调用或数据库驱动不响应取消时，仍需等待该调用返回；自定义源须遵守其取消契约，执行器不会强行中断线程。API 与 Web 已改用 ExecuteAsync；CLI/Desktop 保留实例命令运行器并自动使用统一文件/数据库源。

## 兼容与迁移

- FileParsers、ParseOptions 与 IFileParser 保留为旧调用方适配层，静态解析器列表和重复 JSON/JSONL 实现已删除。新扩展应使用 IRecordParser。
- 新注册表和数据源将 CSV 报为 csv；旧 FileParsers 探测保留把 CSV 识别为 excel 的历史别名。无需依赖该别名决定格式能力。
- 新 JSON 入口严格校验完整文档，不再容忍缺失数组闭合符、尾逗号或尾随垃圾内容。
- XML 在新入口中遵守统一 Flatten 设置，过去被忽略的嵌套字段会按结构化路径展平。
- 执行签名增加 record-source/v1 与错误策略。旧位置检查点、带旧执行签名的基线需要重新分析并重建，不能直接继续累计；外层 v3 和内部 analysis-state/v2 的计算状态序列化版本未改变。
- 内置持久化游标仍限于未压缩 JSONL。其他格式通过能力声明明确拒绝恢复，未承诺任意 SQL 查询或任意记录序列的稳定游标。

回归同时发现并修复 HLL 稀疏状态合并稠密状态时丢失已有寄存器的问题，补充了确定性合并测试。已丢失数据的旧草图无法从快照推回原值，需重新分析源数据。

## 验证

新增 20 项契约测试，覆盖多输入等价性、精确数字与路径、注册隔离、流所有权、原生异步读取、错误跳过、诊断上限、游标、采样、压缩根投影、取消/计算失败后的生产者释放、数据库类型、严格 JSON、跨格式展平、非可寻址 CSV 和 HLL 合并。

完整解决方案 Release 构建零警告、零错误；Core 722/724 通过，剩余两项为原有 SQLite 临时数据库删除时的连接池文件锁失败。API 125/125、Web 74/74 全部通过。
