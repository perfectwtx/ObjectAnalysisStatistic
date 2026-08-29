namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// Web.Tests 项目内 AnsiConsole 顺序集合。
///
/// MemoryBudget.Check 内部调用 Spectre.Console.AnsiConsole.MarkupLine；P6PostAnalysis 间接
/// 触达它；并发跑会争抢全局 AnsiConsole 句柄触发 "Trying to run one or more interactive functions
/// concurrently" 异常。用 xUnit Collection 强制同集合内测试串行，跨集合仍可进程级并行。
///
/// 类名以 "SequentialGroup" 收尾以避开 CA1711（标识符不应以 "Collection" 结尾）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebAnsiConsoleSequentialGroup
{
    public const string Name = "WebAnsiConsoleCollection";
}
