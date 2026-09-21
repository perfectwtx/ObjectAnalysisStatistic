using System.IO;
using System.Text.Json;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

public sealed class LocalAnalysisService
{
    public static async Task<LocalAnalysisResult> AnalyzeAsync(
        string inputPath,
        string reportPath,
        Action<string> writeOutput,
        Action<long> reportProgress,
        DesktopPlatformService platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(platform);

        await platform.InitializeAsync(cancellationToken);

        var alertStore = new SqliteAlertRuleStore(platform.DatabasePath);
        var webhookStore = new SqliteWebhookDeliveryStore(platform.DatabasePath);
        await alertStore.InitializeAsync(cancellationToken);
        await webhookStore.InitializeAsync(cancellationToken);

        var jobService = new AnalysisJobService(
            platform.HistoryStore,
            alertStore,
            webhookStore,
            platform.BaselineStore);

        var coordinator = new AnalysisExecutionCoordinator(jobService);
        var console = new DesktopConsole(writeOutput);
        var progress = new DesktopProgressReporter(reportProgress);

        try
        {
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

            AnalysisResult result = await coordinator.ExecuteAsync(
                Path.GetFileName(inputPath),
                DetectSourceType(inputPath),
                async token =>
                {
                    AnalysisServices.Use(new AnalysisServices
                    {
                        Console = console,
                        Progress = progress,
                        Presenter = NullResultPresenter.Instance
                    });

                    try
                    {
                        int exitCode = await Task.Run(
                            () => AnalysisRunner.Run(options, token),
                            token);

                        if (exitCode == 2 && token.IsCancellationRequested)
                            throw new OperationCanceledException(token);

                        if (exitCode != 0)
                            throw new AnalysisExecutionException(exitCode);

                        if (!File.Exists(reportPath))
                            throw new FileNotFoundException("分析完成但没有生成报告。", reportPath);

                        return LoadResult(reportPath);
                    }
                    finally
                    {
                        AnalysisServices.Use(null);
                    }
                },
                options,
                cancellationToken);

            return new LocalAnalysisResult(0, result);
        }
        catch (AnalysisExecutionException ex)
        {
            return new LocalAnalysisResult(ex.ExitCode, null);
        }
    }

    public static AnalysisResult LoadResult(string reportPath)
    {
        var json = File.ReadAllText(reportPath);
        return JsonSerializer.Deserialize<AnalysisResult>(json)
            ?? throw new InvalidDataException("报告内容为空或不是有效的 AnalysisResult。");
    }

    private static string DetectSourceType(string inputPath)
        => Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant() switch
        {
            "jsonl" => "JSONL",
            "yml" => "YAML",
            "gz" => "GZip",
            "bz2" => "BZip2",
            _ => Path.GetExtension(inputPath).TrimStart('.').ToUpperInvariant()
        };
}

public sealed record LocalAnalysisResult(int ExitCode, AnalysisResult? Result);

internal sealed class AnalysisExecutionException : Exception
{
    public int ExitCode { get; }

    public AnalysisExecutionException(int exitCode)
        : base($"分析结束，退出码 {exitCode}")
    {
        ExitCode = exitCode;
    }
}

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
