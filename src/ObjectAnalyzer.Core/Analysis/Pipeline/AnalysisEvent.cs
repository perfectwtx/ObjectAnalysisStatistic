namespace ObjectAnalyzer.Analysis.Pipeline;

public enum AnalysisStage { Preparing, Input, Projection, Transformation, Filtering, Sampling, Observation, Completion }
public enum AnalysisEventKind { Started, StageStarted, Progress, Diagnostic, Completed, Cancelled, Failed }
public enum AnalysisFailureKind { Configuration, Input, ResourceLimit, Execution }

public sealed record AnalysisDiagnostic(string Code, string Message, IReadOnlyDictionary<string, object?>? Data = null);

public sealed record AnalysisEvent(Guid RunId, AnalysisEventKind Kind, AnalysisStage? Stage = null,
    long? Records = null, AnalysisDiagnostic? Diagnostic = null, AnalysisFailureKind? Failure = null,
    Exception? Exception = null);

internal static class AnalysisFailure
{
    public static AnalysisFailureKind Classify(Exception exception, AnalysisStage stage)
    {
        if (exception is AggregateException aggregate)
        {
            var kinds = aggregate.Flatten().InnerExceptions.Select(item => Classify(item, stage)).Distinct().ToArray();
            return kinds.Length == 1 ? kinds[0] : AnalysisFailureKind.Execution;
        }
        if (exception is MaxMemoryExceededException) return AnalysisFailureKind.ResourceLimit;
        if (stage == AnalysisStage.Preparing) return AnalysisFailureKind.Configuration;
        return exception switch
        {
            ArgumentException or NotSupportedException => AnalysisFailureKind.Configuration,
            IOException or InvalidDataException or FormatException or System.Text.Json.JsonException
                or System.Xml.XmlException or System.Data.Common.DbException => AnalysisFailureKind.Input,
            _ => AnalysisFailureKind.Execution
        };
    }
}
