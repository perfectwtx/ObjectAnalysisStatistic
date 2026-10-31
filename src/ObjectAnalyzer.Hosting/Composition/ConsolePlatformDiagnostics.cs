namespace ObjectAnalyzer.Platform;

internal sealed class ConsolePlatformDiagnostics : IPlatformDiagnostics
{
    public void Report(string message, Exception exception)
        => Console.Error.WriteLine($"[platform] {message}: {exception.GetType().Name}");
}
