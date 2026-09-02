using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>POST /api/preflight 成功响应：把 Core 的 PreflightResult 原样回传。</summary>
public sealed record PreflightResponse(PreflightResult Preflight)
{
    /// <summary>预检结果（hasWarning / ruleFields / matchedRuleFields / sampleFields 等）。</summary>
    public PreflightResult Preflight { get; init; } = Preflight;
}
