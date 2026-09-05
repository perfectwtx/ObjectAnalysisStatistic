using System.Text;
using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 计算两份 <see cref="SchemaSnapshot"/> 之间的差异。
/// 破坏性变更定义：字段被删除、主导类型变化、nullable 收紧（true → false）、coverage 大幅下降（≥ 0.1）。
/// </summary>
public static class SchemaDiffer
{
    /// <summary>coverage 下降阈值（绝对值）。从 1.0 跌到 0.9 视为显著，0.95 → 0.94 不计入。</summary>
    public const double CoverageDropThreshold = 0.1;

    private static readonly JsonSerializerOptions s_loadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static SchemaDiff Compute(SchemaSnapshot baseline, SchemaSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var diff = new SchemaDiff
        {
            BaselineSource = baseline.Source,
            CurrentSource = current.Source
        };

        var baselineByPath = baseline.Fields
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var currentByPath = current.Fields
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 新增字段
        foreach (var (path, field) in currentByPath)
        {
            if (!baselineByPath.ContainsKey(path))
            {
                diff.AddedFields.Add(field);
            }
        }

        // 删除字段（破坏性）
        foreach (var (path, field) in baselineByPath)
        {
            if (!currentByPath.ContainsKey(path))
            {
                diff.RemovedFields.Add(field);
                diff.HasBreakingChanges = true;
            }
        }

        // 变更字段
        foreach (var (path, baselineField) in baselineByPath)
        {
            if (!currentByPath.TryGetValue(path, out var currentField))
                continue; // 已计入 removed

            var change = CompareField(baselineField, currentField);
            if (change.Changes.Count > 0)
            {
                diff.ChangedFields.Add(change);
                if (change.Breaking)
                {
                    diff.HasBreakingChanges = true;
                }
            }
        }

        return diff;
    }

    public static SchemaDiff ComputeFromFiles(string baselinePath, string currentPath)
    {
        var baseline = LoadFromFile(baselinePath);
        var current = LoadFromFile(currentPath);
        return Compute(baseline, current);
    }

    public static SchemaSnapshot LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Schema 快照文件不存在: {path}", path);
        }
        var json = File.ReadAllText(path, Encoding.UTF8);
        var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(json, s_loadOptions);
        if (snapshot == null)
        {
            throw new InvalidDataException($"Schema 快照解析失败: {path}");
        }
        return snapshot;
    }

    private static SchemaFieldChange CompareField(SchemaSnapshotField baseline, SchemaSnapshotField current)
    {
        var change = new SchemaFieldChange { Path = current.Path };

        // primaryType 变化：破坏性
        if (!string.Equals(baseline.PrimaryType, current.PrimaryType, StringComparison.Ordinal))
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "primaryType",
                Breaking = true,
                Description = $"类型变化: {baseline.PrimaryType} → {current.PrimaryType}"
            });
            change.Breaking = true;
        }

        // semanticType 变化（非破坏性；信息性）
        if (!string.Equals(baseline.SemanticType ?? "", current.SemanticType ?? "", StringComparison.Ordinal))
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "semanticType",
                Breaking = false,
                Description = $"语义变化: {baseline.SemanticType ?? "(无)"} → {current.SemanticType ?? "(无)"}"
            });
        }

        // nullable 收紧（true → false）：破坏性
        if (baseline.Nullable && !current.Nullable)
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "nullable",
                Breaking = true,
                Description = "可空性收紧: nullable → non-null（出现 null 的样本不再被接受）"
            });
            change.Breaking = true;
        }
        else if (!baseline.Nullable && current.Nullable)
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "nullable",
                Breaking = false,
                Description = "可空性放宽: non-null → nullable（开始出现 null）"
            });
        }

        // coverage 大幅下降：破坏性
        double coverageDelta = baseline.Coverage - current.Coverage;
        if (coverageDelta >= CoverageDropThreshold)
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "coverage",
                Breaking = true,
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"覆盖率下降: {baseline.Coverage:P1} → {current.Coverage:P1}（Δ={coverageDelta:P1}）")
            });
            change.Breaking = true;
        }
        else if (Math.Abs(coverageDelta) >= 0.01)
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "coverage",
                Breaking = false,
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"覆盖率变化: {baseline.Coverage:P1} → {current.Coverage:P1}（Δ={coverageDelta:+0.0%;-0.0%;0.0%}）")
            });
        }

        // 数值范围：信息性
        if (baseline.MinNumber.HasValue && current.MinNumber.HasValue &&
            !baseline.MinNumber.Value.Equals(current.MinNumber.Value))
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "minNumber",
                Breaking = false,
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"数值下界: {baseline.MinNumber.Value:G6} → {current.MinNumber.Value:G6}")
            });
        }
        if (baseline.MaxNumber.HasValue && current.MaxNumber.HasValue &&
            !baseline.MaxNumber.Value.Equals(current.MaxNumber.Value))
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "maxNumber",
                Breaking = false,
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"数值上界: {baseline.MaxNumber.Value:G6} → {current.MaxNumber.Value:G6}")
            });
        }

        // 异常值比例：信息性
        if (baseline.OutlierRatio.HasValue && current.OutlierRatio.HasValue &&
            Math.Abs(baseline.OutlierRatio.Value - current.OutlierRatio.Value) >= 0.05)
        {
            change.Changes.Add(new SchemaFieldChangeItem
            {
                Property = "outlierRatio",
                Breaking = false,
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"异常值比例: {baseline.OutlierRatio.Value:P1} → {current.OutlierRatio.Value:P1}")
            });
        }

        return change;
    }

    /// <summary>控制台打印 diff 摘要（CLI 模式直接给人看）。</summary>
    public static void PrintSummary(SchemaDiff diff, int maxPerCategory = 10)
    {
        int total = diff.AddedFields.Count + diff.RemovedFields.Count + diff.ChangedFields.Count;
        if (total == 0)
        {
            AnalysisServices.Current.Console.MarkupLine("[green]✓ Schema 无差异（与基线一致）。[/]");
            return;
        }

        string breakingTag = diff.HasBreakingChanges ? "[red]（含破坏性变更）[/]" : "[grey]（非破坏性）[/]";
        AnalysisServices.Current.Console.MarkupLine(
            $"[bold]Schema 差异[/] {breakingTag} — 新增 {diff.AddedFields.Count} / 删除 {diff.RemovedFields.Count} / 变更 {diff.ChangedFields.Count}");

        if (diff.AddedFields.Count > 0)
        {
            AnalysisServices.Current.Console.MarkupLine($"[green]+ 新增字段[/] [grey]({diff.AddedFields.Count})[/]");
            foreach (var f in diff.AddedFields.Take(maxPerCategory))
            {
                AnalysisServices.Current.Console.MarkupLine(
                    $"  [green]+[/] [cyan]{MarkupText.Escape(f.Path)}[/]: {MarkupText.Escape(f.PrimaryType)} [grey](coverage {f.Coverage:P1})[/]");
            }
            if (diff.AddedFields.Count > maxPerCategory)
            {
                AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {diff.AddedFields.Count - maxPerCategory} 个新增字段[/]");
            }
        }

        if (diff.RemovedFields.Count > 0)
        {
            AnalysisServices.Current.Console.MarkupLine($"[red]- 删除字段[/] [grey]({diff.RemovedFields.Count})[/]");
            foreach (var f in diff.RemovedFields.Take(maxPerCategory))
            {
                AnalysisServices.Current.Console.MarkupLine(
                    $"  [red]-[/] [cyan]{MarkupText.Escape(f.Path)}[/]: {MarkupText.Escape(f.PrimaryType)} [grey](coverage {f.Coverage:P1})[/]");
            }
            if (diff.RemovedFields.Count > maxPerCategory)
            {
                AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {diff.RemovedFields.Count - maxPerCategory} 个删除字段[/]");
            }
        }

        if (diff.ChangedFields.Count > 0)
        {
            AnalysisServices.Current.Console.MarkupLine($"[yellow]~ 变更字段[/] [grey]({diff.ChangedFields.Count})[/]");
            foreach (var c in diff.ChangedFields.Take(maxPerCategory))
            {
                string tag = c.Breaking ? "[red]✗[/]" : "[yellow]~[/]";
                AnalysisServices.Current.Console.MarkupLine($"  {tag} [cyan]{MarkupText.Escape(c.Path)}[/]");
                foreach (var item in c.Changes)
                {
                    string itemTag = item.Breaking ? "[red]✗[/]" : "[grey]·[/]";
                    AnalysisServices.Current.Console.MarkupLine($"      {itemTag} {MarkupText.Escape(item.Property)}: {MarkupText.Escape(item.Description)}");
                }
            }
            if (diff.ChangedFields.Count > maxPerCategory)
            {
                AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {diff.ChangedFields.Count - maxPerCategory} 个变更字段[/]");
            }
        }
    }
}
