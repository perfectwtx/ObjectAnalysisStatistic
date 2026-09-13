namespace ObjectAnalyzer.Platform;

/// <summary>在新的成功分析完成后，自动验证上一轮质量问题的整改结果。</summary>
public sealed class QualityIssueRemediationVerifier
{
    private readonly IQualityIssueQuery _issueQuery;
    private readonly IQualityIssueResponsibilityStore _responsibilityStore;
    private readonly IQualityIssueRemediationStore _remediationStore;

    public QualityIssueRemediationVerifier(
        IQualityIssueQuery issueQuery,
        IQualityIssueResponsibilityStore responsibilityStore,
        IQualityIssueRemediationStore remediationStore)
    {
        _issueQuery = issueQuery;
        _responsibilityStore = responsibilityStore;
        _remediationStore = remediationStore;
    }

    /// <summary>
    /// 使用当前分析结果验证上一轮同数据源的问题。
    /// 只有存在整改记录且当前分析中问题已经消失时，才会自动关闭整改与责任状态。
    /// </summary>
    public async Task VerifyAsync(
        AnalysisJob currentJob,
        AnalysisJob? previousJob,
        IReadOnlyList<QualityIssue> currentIssues,
        CancellationToken cancellationToken = default)
    {
        if (previousJob is null || previousJob.Id == currentJob.Id)
            return;

        if (!string.Equals(previousJob.SourceName, currentJob.SourceName, StringComparison.OrdinalIgnoreCase))
            return;

        var previousIssues = await _issueQuery.GetByJobAsync(previousJob.Id, cancellationToken);
        var currentIssueSet = currentIssues
            .Select(x => new IssueKey(x.FieldName, x.Check))
            .ToHashSet(IssueKeyComparer.Instance);

        foreach (var issue in previousIssues)
        {
            if (currentIssueSet.Contains(new IssueKey(issue.FieldName, issue.Check)))
                continue;

            var remediations = await _remediationStore.GetByIssueAsync(
                issue.JobId,
                issue.FieldName,
                issue.Check,
                cancellationToken);
            if (remediations.Count == 0)
                continue;

            foreach (var remediation in remediations.Where(x => !string.Equals(x.Status, "Completed", StringComparison.OrdinalIgnoreCase)))
            {
                var completed = remediation with
                {
                    Status = "Completed",
                    CompletedAtUtc = DateTime.UtcNow
                };
                await _remediationStore.SaveAsync(completed, cancellationToken);
            }

            var responsibility = await _responsibilityStore.GetAsync(
                previousJob.Id,
                issue.FieldName,
                issue.Check,
                cancellationToken);
            if (responsibility is null ||
                string.Equals(responsibility.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(responsibility.Status, "Ignored", StringComparison.OrdinalIgnoreCase))
                continue;

            await _responsibilityStore.SaveAsync(
                responsibility with
                {
                    Status = "Resolved",
                    Comment = AppendVerificationComment(responsibility.Comment),
                    UpdatedAtUtc = DateTime.UtcNow
                },
                cancellationToken);
        }
    }

    private static string AppendVerificationComment(string? comment)
    {
        const string verification = "整改已通过重新分析自动验证。";
        return string.IsNullOrWhiteSpace(comment)
            ? verification
            : $"{comment.Trim()} {verification}";
    }

    private readonly record struct IssueKey(string FieldName, string Check);

    private sealed class IssueKeyComparer : IEqualityComparer<IssueKey>
    {
        public static readonly IssueKeyComparer Instance = new();

        public bool Equals(IssueKey x, IssueKey y) =>
            string.Equals(x.FieldName, y.FieldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Check, y.Check, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(IssueKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FieldName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Check));
    }
}
