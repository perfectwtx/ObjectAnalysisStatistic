namespace ObjectAnalyzer.Tests;

/// <summary>
/// 所有调用 <see cref="AnalysisRunner.Run"/> 的测试必须加入此集合；
/// AnalysisRunner 内部使用 Spectre.Console 的交互式 <c>AnsiConsole.Status()</c>，
/// 并行执行时多个 Status 会争用全局 AnsiConsole 句柄，触发
/// "Trying to run one or more interactive functions concurrently" 异常。
/// 用 xUnit Collection 强制同集合内测试串行，从而与其它集合在进程级并行时仍安全。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalysisRunnerSequentialGroup
{
    public const string Name = "AnalysisRunnerSequentialGroup";
}
