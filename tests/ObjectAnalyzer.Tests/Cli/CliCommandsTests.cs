using System.CommandLine;
using System.Text.Json;
using ObjectAnalyzer.Cli;

namespace ObjectAnalyzer.Tests.Cli;

public sealed class CliCommandsTests
{
    [Fact]
    public void RulesFromSchema_WritesFieldRules()
    {
        using var directory = new TempDirectory();
        string schema = directory.File("schema.json");
        File.WriteAllText(schema, """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer" },
                "name": { "type": "string" }
              },
              "required": ["id"]
            }
            """);
        string output = directory.File("rules.json");

        Command command = CliCommands.CreateRulesFromSchema();
        int exitCode = command.Parse(["--input", schema, "--output", output]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.True(RuleFieldCount(output) > 0);
    }

    [Fact]
    public void RulesFromCode_WritesFieldRules()
    {
        using var directory = new TempDirectory();
        string source = directory.File("User.cs");
        File.WriteAllText(source, """
            namespace Demo;

            public sealed class User
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }
            """);
        string output = directory.File("rules.json");

        Command command = CliCommands.CreateRulesFromCode();
        int exitCode = command.Parse(["--input", source, "--class", "User", "--output", output]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.True(RuleFieldCount(output) > 0);
    }

    private static int RuleFieldCount(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("fields").EnumerateObject().Count();
    }
}
