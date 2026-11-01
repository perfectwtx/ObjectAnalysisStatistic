using System.Globalization;

namespace ObjectAnalyzer.Reporting;

public static partial class MarkdownReportExporter
{
    private static void WriteSchemaDiffSection(TextWriter writer, AnalysisResult result)
    {
        if (result.SchemaDiff == null) return;

        var diff = result.SchemaDiff;
        writer.WriteLine("## Schema 演进");
        writer.WriteLine();
        if (diff.HasBreakingChanges)
        {
            writer.WriteLine("> ⚠️ **含破坏性变更**（删除字段 / 类型变化 / 可空性收紧 / coverage 大幅下降）");
        }
        else
        {
            writer.WriteLine("> ✅ 无破坏性变更");
        }
        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 基线: `{EscapeMd(diff.BaselineSource ?? "(未指定)")}`"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 当前: `{EscapeMd(diff.CurrentSource ?? "(未指定)")}`"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 新增: {diff.AddedFields.Count}，删除: {diff.RemovedFields.Count}，变更: {diff.ChangedFields.Count}"));
        writer.WriteLine();

        if (diff.AddedFields.Count > 0)
        {
            writer.WriteLine("### 新增字段");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 类型 | coverage | 语义 |");
            writer.WriteLine("|------|------|----------|------|");
            foreach (var f in diff.AddedFields)
            {
                writer.Write("| `");
                writer.Write(EscapeMd(f.Path));
                writer.Write("` | ");
                writer.Write(EscapeMd(f.PrimaryType));
                writer.Write(" | ");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write(" | ");
                writer.Write(EscapeMd(f.SemanticType ?? "—"));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }

        if (diff.RemovedFields.Count > 0)
        {
            writer.WriteLine("### 删除字段（破坏性）");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 类型 | coverage | 语义 |");
            writer.WriteLine("|------|------|----------|------|");
            foreach (var f in diff.RemovedFields)
            {
                writer.Write("| `");
                writer.Write(EscapeMd(f.Path));
                writer.Write("` | ");
                writer.Write(EscapeMd(f.PrimaryType));
                writer.Write(" | ");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write(" | ");
                writer.Write(EscapeMd(f.SemanticType ?? "—"));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }

        if (diff.ChangedFields.Count > 0)
        {
            writer.WriteLine("### 变更字段");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 破坏性 | 变化项 |");
            writer.WriteLine("|------|--------|-------|");
            foreach (var c in diff.ChangedFields)
            {
                writer.Write("| `");
                writer.Write(EscapeMd(c.Path));
                writer.Write("` | ");
                writer.Write(c.Breaking ? "**是**" : "否");
                writer.Write(" | ");
                writer.Write(EscapeMd(string.Join(" · ", c.Changes.Select(x => $"{x.Property}: {x.Description}"))));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }
    }
}
