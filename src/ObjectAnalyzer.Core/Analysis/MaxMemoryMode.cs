using System;
using System.Threading;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>--max-memory 超限模式（soft 警告 + 自动采样 / hard 立即失败）。</summary>
public enum MaxMemoryMode
{
    /// <summary>超限仅警告 + 自动启用采样；分析继续。</summary>
    Soft,

    /// <summary>超限立即失败（AnalysisRunner 以退出码 1 结束）。</summary>
    Hard
}
