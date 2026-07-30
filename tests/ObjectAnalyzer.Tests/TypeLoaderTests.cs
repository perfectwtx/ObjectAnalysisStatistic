using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ObjectAnalyzer.Tests;

public class TypeLoaderTests
{
    [Fact]
    public void Compile_ValidCSharpFile_CompilesAndReturnsType()
    {
        // Arrange
        string tempCsFile = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        string code = @"
namespace TestNamespace;

public class PersonTest
{
    public int Id { get; set; }
    public string Name { get; set; }
}
";
        File.WriteAllText(tempCsFile, code);

        string tempJsonFile = Path.GetTempFileName();
        string json = @"[
            {""Id"": 42, ""Name"": ""Alice""},
            {""Id"": 100, ""Name"": ""Bob""}
        ]";
        File.WriteAllText(tempJsonFile, json);

        try
        {
            // Act - Compile
            var (assembly, type) = TypeLoader.Compile(tempCsFile, "PersonTest");

            // Assert - Compile
            Assert.NotNull(assembly);
            Assert.NotNull(type);
            Assert.Equal("PersonTest", type.Name);
            Assert.Equal("TestNamespace.PersonTest", type.FullName);

            // Act - Parse typed JSON
            var result = TypedFileParser.ParseJson(tempJsonFile, type).ToList();

            // Assert - Typed parse
            Assert.Equal(2, result.Count);
            Assert.Equal(42, result[0]["Id"]);
            Assert.Equal("Alice", result[0]["Name"]);
            Assert.Equal(100, result[1]["Id"]);
            Assert.Equal("Bob", result[1]["Name"]);
        }
        finally
        {
            if (File.Exists(tempCsFile)) File.Delete(tempCsFile);
            if (File.Exists(tempJsonFile)) File.Delete(tempJsonFile);
        }
    }

    [Fact]
    public void Compile_InvalidCSharpFile_ThrowsCompilationException()
    {
        // Arrange
        string tempCsFile = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        string invalidCode = @"
namespace TestNamespace;

public class PersonTest
{
    public int Id { get; set; // Missing closing brace
}
";
        File.WriteAllText(tempCsFile, invalidCode);

        try
        {
            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() =>
                TypeLoader.Compile(tempCsFile, "PersonTest"));
            Assert.Contains("编译 .cs 文件失败", exception.Message);
        }
        finally
        {
            if (File.Exists(tempCsFile)) File.Delete(tempCsFile);
        }
    }
}
