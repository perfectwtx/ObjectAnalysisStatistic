using System.CommandLine;
using ObjectAnalyzer;
using ObjectAnalyzer.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 宿主端口接线：CLI 用 Spectre 富渲染（进度条/表格），Core 通过 Abstractions 端口回调
var analysisRunner = new AnalysisCommandRunner(new()
{
    Console = new SpectreConsoleAdapter(),
    Progress = new SpectreProgressReporter(),
    Presenter = new ConsoleReporter(),
});

// 支持 Ctrl+C 优雅取消长任务
var cts = new CancellationTokenSource();

var cliOptions = new CliOptions();
var rootCommand = new RootCommand("对象字段分析统计工具 (Object Analyzer)");
cliOptions.AddTo(rootCommand);
rootCommand.Subcommands.Add(CliCommands.CreateKmsSecret());
rootCommand.Subcommands.Add(CliCommands.CreateRulesFromCode());
rootCommand.Subcommands.Add(CliCommands.CreateRulesFromSchema());

rootCommand.SetAction(parseResult =>
{
    CliArguments cli = cliOptions.Read(parseResult);
    return CliRunner.Run(cli, analysisRunner, cts.Token);
});

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return rootCommand.Parse(args).Invoke();
