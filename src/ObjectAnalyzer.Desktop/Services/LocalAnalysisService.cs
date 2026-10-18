using System.IO;
using System.Text.Json;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Platform;
using ObjectAnalyzer.Reporting;

namespace ObjectAnalyzer.Desktop.Services;

public sealed class LocalAnalysisService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LocalAnalysisResult> AnalyzeAsync(
        string inputPath,
        string jsonReportPath,
        string userReportPath,
        string? rulesPath,
        DesktopAnalysisSettings settings,
        Action<string> writeOutput,
        Action<long> reportProgress,
        DesktopPlatformService platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonReportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userReportPath);
        ArgumentNullException.ThrowIfNull(settings);
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
                Input = new InputSourceOptions
                {
                    InputPath = inputPath
                },
                Rules = new RulesOptions
                {
                    RulesFile = string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath
                },
                Report = new ReportOutputOptions
                {
                    OutputPath = jsonReportPath
                },
                Progress = new ProgressOptions
                {
                    ShowProgress = true
                },
                Advanced = new AdvancedAnalysisOptions
                {
                    EnableCorrelation = settings.EnableCorrelation,
                    EnableStringPatterns = settings.EnableStringPatterns,
                    EnableUnicodeHygiene = settings.EnableUnicodeHygiene,
                    EnableTimeSeries = settings.EnableTimeSeries,
                    EnableDistributionSnapshot = settings.EnableDistributionSnapshot,
                    FuzzyDedupField = string.IsNullOrWhiteSpace(settings.FuzzyDedupField)
                        ? null
                        : settings.FuzzyDedupField.Trim()
                },
                Sampling = new SamplingOptions
                {
                    SampleReservoir = settings.SampleReservoir > 0 ? settings.SampleReservoir : 0
                }
            };

            AnalysisResult result = await coordinator.ExecuteAsync(
                Path.GetFileName(inputPath),
                DetectSourceType(inputPath),
                async token =>
                {
                    var runner = new AnalysisCommandRunner(new AnalysisServices
                    {
                        Console = console,
                        Progress = progress,
                        Presenter = NullResultPresenter.Instance
                    });

                    int exitCode = await Task.Run(
                        () => runner.Run(options, token),
                        token);

                    if (exitCode == 2 && token.IsCancellationRequested)
                        throw new OperationCanceledException(token);

                    if (exitCode != 0)
                        throw new AnalysisExecutionException(exitCode);

                    if (!File.Exists(jsonReportPath))
                        throw new FileNotFoundException("分析完成但没有生成 JSON 报告。", jsonReportPath);

                    AnalysisResult analysisResult = LoadResult(jsonReportPath);

                    if (!string.Equals(
                            Path.GetFullPath(jsonReportPath),
                            Path.GetFullPath(userReportPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(userReportPath)!);
                        ReportExporter.Save(analysisResult, userReportPath, redactPii: true);
                        writeOutput($"已导出用户格式报告: {userReportPath}");
                    }

                    return analysisResult;
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
        return JsonSerializer.Deserialize<AnalysisResult>(json, s_jsonOptions)
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

public sealed class DesktopAnalysisSettings
{
    public bool EnableCorrelation { get; set; }
    public bool EnableStringPatterns { get; set; }
    public bool EnableUnicodeHygiene { get; set; }
    public bool EnableTimeSeries { get; set; }
    public bool EnableDistributionSnapshot { get; set; }
    public string FuzzyDedupField { get; set; } = string.Empty;
    public int SampleReservoir { get; set; }
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
            @"\[\[|\]\]|\[(?:/|/?(?:bold|dim|italic|underline|invert|strikethrough|red|green|blue|yellow|grey|gray|white|black|cyan|magenta|orange\d*|#\w+)(?:[ a-zA-Z0-9#;,.():-]*)?)\]",
            match => match.Value switch
            {
                "[[" => "[",
                "]]" => "]",
                _ => string.Empty
            });
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
