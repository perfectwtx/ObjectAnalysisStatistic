using System.Text.Json;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Desktop.Services;

public sealed class LocalAnalysisService
{
    public async Task<LocalAnalysisResult> AnalyzeAsync(
        string inputPath,
        string reportPath,
        Action<string> writeOutput,
        Action<long> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        var console = new DesktopConsole(writeOutput);
        var progress = new DesktopProgressReporter(reportProgress);

        AnalysisServices.Use(new AnalysisServices
        {
            Console = console,
            Progress = progress,
            Presenter = NullResultPresenter.Instance
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var options = new AnalysisOptions
            {
                Input = new ObjectAnalyzer.Analysis.Options.InputSourceOptions
                {
                    InputPath = inputPath
                },
                Report = new ObjectAnalyzer.Analysis.Options.ReportOutputOptions
                {
                    OutputPath = reportPath
                },
                Progress = new ObjectAnalyzer.Analysis.Options.ProgressOptions
                {
                    ShowProgress = true
                }
            };

            int exitCode = await Task.Run(() => AnalysisRunner.Run(options), cancellationToken);
            if (exitCode != 0)
                return new LocalAnalysisResult(exitCode, null);

            if (!File.Exists(reportPath))
                throw new FileNotFoundException("分析完成但没有生成报告。", reportPath);

            return new LocalAnalysisResult(exitCode, LoadResult(reportPath));
        }
        finally
        {
            AnalysisServices.Use(null);
        }
    }

    public static AnalysisResult LoadResult(string reportPath)
    {
        var json = File.ReadAllText(reportPath);
        return JsonSerializer.Deserialize<AnalysisResult>(json)
            ?? throw new InvalidDataException("报告内容为空或不是有效的 AnalysisResult。");
    }
}

public sealed record LocalAnalysisResult(int ExitCode, AnalysisResult? Result);

internal sealed class DesktopConsole : IAnalysisConsole
{
    private readonly Action<string> _write;

    public DesktopConsole(Action<string> write) => _write = write;

    public void MarkupLine(string markup) => _write(StripMarkup(markup));
    public void WriteLine(string text = "") => _write(text);
    public void Write(string text) => _write(text);

    private static string StripMarkup(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\[/?[a-zA-Z0-9 #;,.:-]+\]",
            string.Empty);
}

internal sealed class DesktopProgressReporter : IProgressReporter
{
    private readonly Action<long> _report;
    private long _lastValue;

    public DesktopProgressReporter(Action<long> report) => _report = report;

    public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
    {
        var progress = new Progress<long>(value =>
        {
            _lastValue = value;
            if (maxObjects is > 0)
                _report((long)((double)value / maxObjects.Value * 100));
        });

        work(showProgress ? progress : null);

        if (maxObjects is null || maxObjects <= 0)
            _report(_lastValue > 0 ? 100 : 0);
    }
}
