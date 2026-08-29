using System;
using System.Threading;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>P6.3：--max-memory hard 模式超限时抛出，由 AnalysisRunner 捕获并以退出码 1 退出。</summary>
public sealed class MaxMemoryExceededException : Exception
{
    public MaxMemoryExceededException(string message) : base(message) { }
}
