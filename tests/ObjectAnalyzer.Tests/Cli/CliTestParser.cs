using System.CommandLine;
using ObjectAnalyzer.Cli;

namespace ObjectAnalyzer.Tests.Cli;

internal static class CliTestParser
{
    public static CliArguments Parse(params string[] args)
    {
        var options = new CliOptions();
        var root = new RootCommand("test");
        options.AddTo(root);

        ParseResult result = root.Parse(args);
        Assert.Empty(result.Errors);
        return options.Read(result);
    }
}
