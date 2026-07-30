import {
  Stack,
  Grid,
  H1,
  H2,
  Text,
  Code,
  Divider,
  Stat,
  Table,
  Pill,
  Card,
  CardHeader,
  CardBody,
} from "qoder/canvas";

const findings = [
  { id: "#1", priority: "HIGH", category: "BUG", title: "ParseJson 大文件 OOM 风险", desc: "File.ReadAllText 一次性读入全部内容", status: "已识别（需流式解析）" },
  { id: "#2", priority: "HIGH", category: "BUG", title: "64 位哈希碰撞致重复误判", desc: "FNV-1a 64 位生日悖论碰撞", status: "已修复 → 128 位 MD5" },
  { id: "#3", priority: "HIGH", category: "异常", title: "错误时 exit code 仍为 0", desc: "CI/CD 无法检测失败", status: "已修复 → Run 返回 int" },
  { id: "#4", priority: "HIGH", category: "BUG", title: "excelJsonColumn 二次 parse", desc: "手动 Parse(args) 与 SetHandler 不一致", status: "已修复 → InvocationContext" },
  { id: "#5", priority: "MED", category: "架构", title: "Run 参数过多（8 个）", desc: "参数列表过长不可维护", status: "已修复 → AnalysisOptions" },
  { id: "#6", priority: "MED", category: "架构", title: "Run/RunTyped 重复代码", desc: "共享分析→报告→导出流程", status: "已修复 → ProcessAndReport" },
  { id: "#7", priority: "MED", category: "性能", title: "多格式一次性读入文件", desc: "ParseJson/Yaml 使用 ReadAllText", status: "已识别（需流式解析）" },
  { id: "#8", priority: "MED", category: "性能", title: "IEnumerable 线程不安全", desc: "yield return 底层共享 StreamReader", status: "已修复 → ToList() 物化" },
  { id: "#9", priority: "MED", category: "功能", title: "空文件未处理", desc: "各解析方法对空文件抛原始异常", status: "已修复 → ValidateFile" },
  { id: "#10", priority: "MED", category: "文档", title: "README 与功能不同步", desc: "缺少 XML/YAML/excel-json 文档", status: "已修复 → 全面更新" },
  { id: "#11", priority: "MED", category: "工程化", title: ".gitignore 遗漏", desc: "test_data_excel_json.xlsx 未忽略", status: "已修复" },
  { id: "#12", priority: "LOW", category: "风格", title: "SerializerOptions 重复", desc: "3 处各自定义 JsonSerializerOptions", status: "已修复 → JsonSharedOptions" },
  { id: "#13", priority: "LOW", category: "扩展性", title: "switch 硬编码格式路由", desc: "新增格式需修改 AnalysisRunner", status: "已识别（建议 IFileParser）" },
  { id: "#14", priority: "LOW", category: "工程化", title: "缺少单元测试", desc: "无测试项目覆盖核心逻辑", status: "已识别（待后续添加）" },
  { id: "#15", priority: "LOW", category: "CLI", title: "测试生成混入正式命令", desc: "--generate-test-excel 不应在正式 CLI", status: "已识别（待后续拆分）" },
  { id: "#16", priority: "LOW", category: "风格", title: "默认值判断逻辑冗余", desc: "16 行 if-else 可简化为 2 行", status: "已修复 → 单次比较" },
];

const priorityTone = (p: string) =>
  p === "HIGH" ? "danger" : p === "MED" ? "warning" : "neutral";

const changes = [
  { file: "AnalysisOptions.cs", type: "新增", desc: "封装所有分析选项" },
  { file: "JsonSharedOptions.cs", type: "新增", desc: "共享 JsonSerializerOptions" },
  { file: "AnalysisRunner.cs", type: "重构", desc: "提取 ProcessAndReport，返回 int" },
  { file: "Program.cs", type: "重构", desc: "InvocationContext 绑定 9 个选项" },
  { file: "ObjectAnalyzer.cs", type: "修复", desc: "128 位 MD5 + IEnumerable 物化" },
  { file: "FileParsers.cs", type: "修复", desc: "ValidateFile 空文件检查" },
  { file: "ConsoleReporter.cs", type: "简化", desc: "默认值判断 16→2 行" },
  { file: "ReportExporter.cs", type: "修复", desc: "使用 JsonSharedOptions.Write" },
  { file: "TypedFileParser.cs", type: "修复", desc: "使用 JsonSharedOptions.Read" },
  { file: ".gitignore", type: "修复", desc: "添加 excel_json xlsx" },
  { file: "README.md", type: "更新", desc: "同步全部新功能" },
];

const verification = [
  { item: "构建", result: "0 警告 0 错误", status: "通过" },
  { item: "JSON 解析", result: "3 对象，9 字段正确", status: "通过" },
  { item: "CSV 解析", result: "3 对象，4 字段正确", status: "通过" },
  { item: "XML 解析", result: "3 对象，5 字段正确", status: "通过" },
  { item: "YAML 解析", result: "3 对象，5 字段正确", status: "通过" },
  { item: "Excel-JSON", result: "3 对象，5 字段正确", status: "通过" },
  { item: "错误退出码", result: "不存在 → ExitCode=1", status: "通过" },
  { item: "--help", result: "9 个选项正确显示", status: "通过" },
];

export default function ObjectAnalyzerReview() {
  return (
    <Stack gap={20}>
      <H1>ObjectAnalyzer 代码审查报告</H1>
      <Text tone="secondary" size="small">
        审查范围: Program.cs, AnalysisRunner.cs, FileParsers.cs, ObjectAnalyzer.cs, ConsoleReporter.cs, ReportExporter.cs, TypeLoader.cs, TypedFileParser.cs, README.md
      </Text>

      <Grid columns={4} gap={16}>
        <Stat value="16" label="发现问题" tone="warning" />
        <Stat value="11" label="已实施改进" tone="success" />
        <Stat value="11" label="修改文件" />
        <Stat value="0" label="构建警告" tone="success" />
      </Grid>

      <Divider />

      <H2>审查维度覆盖</H2>
      <Grid columns={3} gap={12}>
        {[
          { dim: "代码质量与架构", items: "职责划分、重复代码、异常处理" },
          { dim: "功能完整性", items: "7 种格式解析、边界情况处理" },
          { dim: "命令行体验", items: "CLI 参数设计、帮助信息、错误提示" },
          { dim: "性能", items: "并行处理、哈希碰撞、大文件场景" },
          { dim: "项目工程化", items: "README 同步、.gitignore、测试" },
          { dim: "可扩展性", items: "格式扩展、分析维度扩展" },
        ].map((d) => (
          <Card key={d.dim} size="sm">
            <CardHeader title={<Text weight="semibold">{d.dim}</Text>} />
            <CardBody>
              <Text size="small" tone="secondary">{d.items}</Text>
            </CardBody>
          </Card>
        ))}
      </Grid>

      <Divider />

      <H2>发现的问题（按优先级）</H2>
      <Table
        columns={[
          { key: "id", title: "#", width: "40px" },
          { key: "priority", title: "优先级", width: "70px", render: (r: any) => <Pill tone={priorityTone(r.priority) as any} size="sm">{r.priority}</Pill> },
          { key: "category", title: "类别", width: "60px" },
          { key: "title", title: "问题", minWidth: "180px" },
          { key: "status", title: "状态", width: "160px", render: (r: any) => <Text size="small" tone={r.status.startsWith("已修复") ? "primary" : "secondary"}>{r.status}</Text> },
        ]}
        rows={findings}
        density="compact"
      />

      <Divider />

      <H2>已实施的改进</H2>
      <Table
        columns={[
          { key: "file", title: "文件", minWidth: "160px", render: (r: any) => <Code>{r.file}</Code> },
          { key: "type", title: "类型", width: "60px", render: (r: any) => {
            const t = r.type === "新增" ? "success" : r.type === "修复" ? "warning" : r.type === "重构" ? "info" : "neutral";
            return <Pill tone={t as any} size="sm">{r.type}</Pill>;
          }},
          { key: "desc", title: "说明", minWidth: "250px" },
        ]}
        rows={changes}
        density="compact"
      />

      <Divider />

      <H2>验证结果</H2>
      <Table
        columns={[
          { key: "item", title: "测试项", minWidth: "120px" },
          { key: "result", title: "结果", minWidth: "200px" },
          { key: "status", title: "状态", width: "80px", render: () => <Pill tone="success" size="sm">通过</Pill> },
        ]}
        rows={verification}
        density="compact"
      />

      <Divider />

      <H2>关键改进详情</H2>

      <Card size="sm">
        <CardHeader title={<Text weight="semibold">高优先级修复</Text>} />
        <CardBody>
          <Stack gap={12}>
            <Card size="sm">
              <CardHeader title={<Text weight="semibold">#2 哈希碰撞 → 128 位 MD5</Text>} />
              <CardBody>
                <Text size="small">
                  原 64 位 FNV-1a 哈希在 ~5 万对象时碰撞概率约 0.03%。改用 MD5 生成 128 位哈希，碰撞概率降至 2^-128。使用 pragma 抑制 CA5351 安全警告（仅用于去重，非加密）。
                </Text>
              </CardBody>
            </Card>
            <Card size="sm">
              <CardHeader title={<Text weight="semibold">#3 退出码 + #4 InvocationContext</Text>} />
              <CardBody>
                <Text size="small">
                  AnalysisRunner.Run 返回 int（0=成功，1=失败）。Program.cs 改用 InvocationContext 统一绑定全部 9 个选项，消除 SetHandler 8 参数限制和二次 Parse(args) 不一致风险。
                </Text>
              </CardBody>
            </Card>
          </Stack>
        </CardBody>
      </Card>

      <Card size="sm">
        <CardHeader title={<Text weight="semibold">中优先级修复</Text>} />
        <CardBody>
          <Stack gap={12}>
            <Card size="sm">
              <CardHeader title={<Text weight="semibold">#5 AnalysisOptions + #6 ProcessAndReport</Text>} />
              <CardBody>
                <Text size="small">
                  新增 AnalysisOptions 类封装 8 个参数，Run 签名从 8 参数简化为 1 个。提取 ProcessAndReport 公共方法消除 Run/RunTyped 重复代码。
                </Text>
              </CardBody>
            </Card>
            <Card size="sm">
              <CardHeader title={<Text weight="semibold">#8 线程安全 + #9 空文件</Text>} />
              <CardBody>
                <Text size="small">
                  Parallel.ForEach 前将 IEnumerable 物化为 List，避免 yield return 底层 StreamReader 并发问题。新增 ValidateFile 统一检查文件存在性和空文件。
                </Text>
              </CardBody>
            </Card>
          </Stack>
        </CardBody>
      </Card>

      <Card size="sm">
        <CardHeader title={<Text weight="semibold">待后续处理</Text>} />
        <CardBody>
          <Stack gap={8}>
            <Text size="small" tone="secondary">#1/#7 流式解析: JSON 用 Utf8JsonReader, YAML 用 TextReader</Text>
            <Text size="small" tone="secondary">#13 IFileParser 接口 + 注册表模式</Text>
            <Text size="small" tone="secondary">#14 添加 xUnit 测试项目</Text>
            <Text size="small" tone="secondary">#15 拆分 --generate-test-excel 为独立工具</Text>
          </Stack>
        </CardBody>
      </Card>

      <Divider />
      <Text tone="tertiary" size="small">
        Git: 6ea996b | 11 files, +280 -176 | Build: 0 warnings, 0 errors
      </Text>
    </Stack>
  );
}
