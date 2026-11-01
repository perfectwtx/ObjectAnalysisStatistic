using System.Globalization;

namespace ObjectAnalyzer.Reporting;

public static partial class HtmlReportExporter
{
    private static void WriteSchemaDiffSection(TextWriter writer, AnalysisResult result)
    {
        if (result.SchemaDiff == null) return;

        var diff = result.SchemaDiff;
        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>Schema 演进（vs 基线）</h2>");
        if (diff.HasBreakingChanges)
        {
            writer.WriteLine("<p class=\"quality-fail\">⚠️ <strong>含破坏性变更</strong>（删除字段 / 类型变化 / 可空性收紧 / coverage 大幅下降）</p>");
        }
        else
        {
            writer.WriteLine("<p class=\"quality-pass\">✅ 无破坏性变更</p>");
        }
        writer.Write("<p>基线: <code>");
        writer.Write(Escape(diff.BaselineSource ?? "(未指定)"));
        writer.Write("</code>，当前: <code>");
        writer.Write(Escape(diff.CurrentSource ?? "(未指定)"));
        writer.Write("</code></p>");
        writer.Write("<p>新增: ");
        writer.Write(diff.AddedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.Write("，删除: ");
        writer.Write(diff.RemovedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.Write("，变更: ");
        writer.Write(diff.ChangedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("</p>");

        if (diff.AddedFields.Count > 0)
        {
            writer.WriteLine("<h3>新增字段</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>类型</th><th>coverage</th><th>语义</th></tr></thead><tbody>");
            foreach (var f in diff.AddedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(f.Path));
                writer.Write("</code></td><td>");
                writer.Write(Escape(f.PrimaryType));
                writer.Write("</td><td>");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write("</td><td>");
                writer.Write(Escape(f.SemanticType ?? "—"));
                writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.RemovedFields.Count > 0)
        {
            writer.WriteLine("<h3>删除字段（破坏性）</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>类型</th><th>coverage</th><th>语义</th></tr></thead><tbody>");
            foreach (var f in diff.RemovedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(f.Path));
                writer.Write("</code></td><td>");
                writer.Write(Escape(f.PrimaryType));
                writer.Write("</td><td>");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write("</td><td>");
                writer.Write(Escape(f.SemanticType ?? "—"));
                writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.ChangedFields.Count > 0)
        {
            writer.WriteLine("<h3>变更字段</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>破坏性</th><th>变化项</th></tr></thead><tbody>");
            foreach (var c in diff.ChangedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(c.Path));
                writer.Write("</code></td><td>");
                writer.Write(c.Breaking ? "<strong style=\"color:#d32f2f\">是</strong>" : "否");
                writer.Write("</td><td><ul>");
                foreach (var item in c.Changes)
                {
                    string color = item.Breaking ? "#d32f2f" : "#666";
                    writer.Write("<li style=\"color:");
                    writer.Write(color);
                    writer.Write("\"><code>");
                    writer.Write(Escape(item.Property));
                    writer.Write("</code>: ");
                    writer.Write(Escape(item.Description));
                    writer.WriteLine("</li>");
                }
                writer.WriteLine("</ul></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        writer.WriteLine("</div>");
    }
}
