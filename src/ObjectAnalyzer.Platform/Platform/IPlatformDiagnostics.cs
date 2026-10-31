namespace ObjectAnalyzer.Platform;

/// <summary>Host-supplied sink for platform diagnostics that must not depend on a specific logging stack.</summary>
public interface IPlatformDiagnostics
{
    void Report(string message, Exception exception);
}

/// <summary>Discards diagnostics. Used when the host does not configure a sink.</summary>
public sealed class NullPlatformDiagnostics : IPlatformDiagnostics
{
    public static NullPlatformDiagnostics Instance { get; } = new();

    private NullPlatformDiagnostics()
    {
    }

    public void Report(string message, Exception exception)
    {
    }
}
