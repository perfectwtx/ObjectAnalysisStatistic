using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Data;

// This application references only the kernel: no parsers, database drivers, PDF or platform.
var state = new AnalysisAccumulator();
foreach (var amount in new[] { 10, 20, 30 })
    state.Observe(new Dictionary<FieldPath, RecordValue>
    {
        [FieldPath.Parse("amount")] = RecordValue.FromObject(amount)
    });
var result = state.Complete();
var field = result.FieldStatistics.Single();
var schema = SchemaInference.Infer(result);
if (result.TotalObjects != 3 || field.AvgNumber != 20 || schema.Root.Properties.Length != 1)
    throw new InvalidOperationException("In-memory analysis failed.");
Console.WriteLine($"records={result.TotalObjects}, average={field.AvgNumber}, schema={schema.Version}");
