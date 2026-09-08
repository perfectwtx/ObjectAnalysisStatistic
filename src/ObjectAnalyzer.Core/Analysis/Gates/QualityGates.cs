namespace ObjectAnalyzer.Analysis.Gates;

/// <summary>
/// W4 门禁注册表：一条 CI 门禁一个实现。新增门禁 = 注册新实现，退出码是门禁的属性（数据），
/// 不再是 AnalysisRunner 的 if/else 硬编码分支。
/// </summary>
public interface IQualityGate
{
    /// <summary>门禁名（审计 / 日志展示用）。</summary>
    string Name { get; }

    /// <summary>门禁未通过时对应的 CI 退出码（3=数据质量，4=数据安全合规，5=异常值，6=Schema 演进）。</summary>
    int ExitCode { get; }

    /// <summary>门禁未通过时的审计摘要。</summary>
    string FailureMessage { get; }

    /// <summary>评估门禁。通过返回 <see cref="GateVerdict"/>（Triggered=false）。</summary>
    GateVerdict Check(AnalysisResult result, AnalysisOptions options);
}

/// <summary>门禁判定结果。</summary>
public readonly record struct GateVerdict(bool Triggered)
{
    public static readonly GateVerdict Pass = new(false);
}

/// <summary>
/// 门禁注册表：静态初始化全部内置门禁，按退出码升序评估——首个触发的门禁决定 CI 退出码
/// （与历史 if/else 链语义一致：3 → 4 → 5 → 6）。
/// </summary>
public static class QualityGateRegistry
{
    private static readonly List<IQualityGate> s_gates =
    [
        new QualityViolationGate(),
        new PiiComplianceGate(),
        new OutlierRatioGate(),
        new SchemaEvolutionGate(),
    ];

    /// <summary>全部已注册门禁（按评估顺序）。</summary>
    public static IReadOnlyList<IQualityGate> Gates => s_gates;

    /// <summary>注册自定义门禁（插在队首，先注册先评估）。</summary>
    public static void Register(IQualityGate gate) => s_gates.Insert(0, gate);

    /// <summary>
    /// 依序评估全部门禁，返回首个触发门禁的退出码与审计摘要；全部通过时返回 (0, null)。
    /// </summary>
    public static (int ExitCode, string? FailureMessage) Evaluate(AnalysisResult result, AnalysisOptions options)
    {
        foreach (var gate in s_gates)
        {
            if (gate.Check(result, options).Triggered)
            {
                return (gate.ExitCode, gate.FailureMessage);
            }
        }
        return (0, null);
    }
}

/// <summary>数据质量门禁（退出码 3）：存在任一质量违规（枚举/范围/模式/必填/唯一/null 率/数据集期望）时触发。</summary>
public sealed class QualityViolationGate : IQualityGate
{
    public string Name => "数据质量";
    public int ExitCode => 3;
    public string FailureMessage => "数据质量门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        if (!options.Gates.FailOnViolations || result.QualityViolations.Count == 0)
            return GateVerdict.Pass;

        long occurrenceCount = result.QualityViolations.Sum(v => v.Count);
        AnalysisServices.Current.Console.MarkupLine(
            $"[red]数据质量未通过（{result.QualityViolations.Count} 条质量违规 / 涉及 {occurrenceCount} 行或值），退出码 3。[/]");
        foreach (var violation in result.QualityViolations.Take(5))
        {
            string scope = string.IsNullOrEmpty(violation.Field)
                ? QualityCheckTypes.Label(violation.Check)
                : $"{violation.Field}: {QualityCheckTypes.Label(violation.Check)}";
            AnalysisServices.Current.Console.MarkupLine($"  [red]·[/] {MarkupText.Escape(scope)} — {MarkupText.Escape(violation.Message)}");
        }

        if (result.QualityViolations.Count > 5)
        {
            AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {result.QualityViolations.Count - 5} 条，详见 --violations 导出[/]");
        }

        return new GateVerdict(true);
    }
}

/// <summary>数据安全合规门禁（退出码 4）：合规等级达到或劣于阈值时触发（与 --fail-on-violations 对称）。</summary>
public sealed class PiiComplianceGate : IQualityGate
{
    public string Name => "数据安全合规";
    public int ExitCode => 4;
    public string FailureMessage => "数据安全合规门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Gates.FailOnPiiLevel))
            return GateVerdict.Pass;

        var piiReport = PiiReport.Build(result);
        if (piiReport.PiiFieldCount == 0 ||
            PiiSeverityRank(piiReport.ComplianceLevel) < PiiSeverityRank(options.Gates.FailOnPiiLevel))
            return GateVerdict.Pass;

        AnalysisServices.Current.Console.MarkupLine(
            $"[red]数据安全合规未通过（合规等级 {piiReport.ComplianceLevel}，发现 {piiReport.PiiFieldCount} 个敏感字段 / {piiReport.PiiValueCount} 个敏感值），退出码 4。[/]");
        return new GateVerdict(true);
    }

    /// <summary>合规等级严重度排序：A=0（干净）&lt; B=1（敏感已打码）&lt; C=2（需人工复核）。</summary>
    internal static int PiiSeverityRank(string level) => level switch
    {
        "C" => 2,
        "B" => 1,
        _ => 0
    };
}

/// <summary>异常值门禁（退出码 5）：任一数值字段异常值比例超阈值时触发。</summary>
public sealed class OutlierRatioGate : IQualityGate
{
    public string Name => "异常值";
    public int ExitCode => 5;
    public string FailureMessage => "异常值门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        if (!options.Gates.FailOnOutlierRatio.HasValue)
            return GateVerdict.Pass;

        double threshold = options.Gates.FailOnOutlierRatio.Value;
        var offenders = result.FieldStatistics
            .Where(s => s.OutlierRatio.HasValue && s.OutlierRatio.Value > threshold)
            .ToList();
        if (offenders.Count == 0)
            return GateVerdict.Pass;

        AnalysisServices.Current.Console.MarkupLine(
            $"[red]异常值未通过（阈值 {threshold:P1}，发现 {offenders.Count} 个字段超限），退出码 5。[/]");
        foreach (var s in offenders.Take(5))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"  [red]·[/] [cyan]{MarkupText.Escape(s.FieldName)}[/]: {s.OutlierRatio:P1} (区间 {MarkupText.Escape(s.OutlierBounds ?? "-")}, {s.OutlierCount} 样本, {s.OutlierMethod} 近似)");
        }
        if (offenders.Count > 5)
        {
            AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {offenders.Count - 5} 个字段，详见报告[/]");
        }

        return new GateVerdict(true);
    }
}

/// <summary>Schema 演进门禁（退出码 6）：--allow-breaking-schema-changes=false 且含破坏性变更时触发。</summary>
public sealed class SchemaEvolutionGate : IQualityGate
{
    public string Name => "Schema 演进";
    public int ExitCode => 6;
    public string FailureMessage => "Schema 演进门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        if (options.Gates.AllowBreakingSchemaChanges || result.SchemaDiff is not { HasBreakingChanges: true })
            return GateVerdict.Pass;

        int removed = result.SchemaDiff.RemovedFields.Count;
        int breakingChanges = result.SchemaDiff.ChangedFields.Count(c => c.Breaking);
        AnalysisServices.Current.Console.MarkupLine(
            $"[red]Schema 演进门禁未通过（--allow-breaking-schema-changes=false，发现 {removed} 个删除字段、{breakingChanges} 个破坏性变更字段），退出码 6。[/]");
        foreach (var f in result.SchemaDiff.RemovedFields.Take(5))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"  [red]-[/] [cyan]{MarkupText.Escape(f.Path)}[/]: {MarkupText.Escape(f.PrimaryType)} (coverage {f.Coverage:P1})");
        }
        foreach (var c in result.SchemaDiff.ChangedFields.Where(c => c.Breaking).Take(5))
        {
            AnalysisServices.Current.Console.MarkupLine($"  [red]~[/] [cyan]{MarkupText.Escape(c.Path)}[/]");
            foreach (var item in c.Changes)
            {
                if (item.Breaking)
                {
                    AnalysisServices.Current.Console.MarkupLine($"      [red]·[/] {MarkupText.Escape(item.Property)}: {MarkupText.Escape(item.Description)}");
                }
            }
        }

        return new GateVerdict(true);
    }
}
