namespace ObjectAnalyzer.Platform;

/// <summary>Independent outcome of post-analysis remediation verification.</summary>
public sealed record RemediationVerificationOutcome(Guid JobId, Guid? PreviousJobId,
    bool Succeeded, string? Error, DateTime AttemptedAtUtc);
