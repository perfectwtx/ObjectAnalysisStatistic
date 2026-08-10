using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ObjectAnalyzer.Tests;

public class FileParsersTests
{
    [Fact]
    public void ParseJson_ArrayOfObjects_ParsesCorrectly()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string json = @"[
            {""Id"": 1, ""Name"": ""Alice"", ""Info"": {""City"": ""New York""}},
            {""Id"": 2, ""Name"": ""Bob""}
        ]";
        File.WriteAllText(tempFile, json);

        try
        {
            // Act - Flattened
            var resultFlat = FileParsers.ParseByFormat(tempFile, "json", new() { Flatten = true }).ToList();
            Assert.Equal(2, resultFlat.Count);
            Assert.Equal(1L, resultFlat[0]["Id"]);
            Assert.Equal("Alice", resultFlat[0]["Name"]);
            Assert.Equal("New York", resultFlat[0]["Info.City"]);

            // Act - Unflattened
            var resultUnflat = FileParsers.ParseByFormat(tempFile, "json", new() { Flatten = false }).ToList();
            Assert.Equal(2, resultUnflat.Count);
            Assert.Equal(1L, resultUnflat[0]["Id"]);
            Assert.IsType<Dictionary<string, object?>>(resultUnflat[0]["Info"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseJson_WithUtf8Bom_ParsesCorrectly()
    {
        // Arrange - UTF-8 BOM (EF BB BF) 开头的 JSON，模拟 Windows/Excel 保存的文件
        string tempFile = Path.GetTempFileName();
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        string json = "[{\"Id\": 1, \"Name\": \"Alice\"}]";
        byte[] bytes = new byte[bom.Length + System.Text.Encoding.UTF8.GetByteCount(json)];
        bom.CopyTo(bytes, 0);
        System.Text.Encoding.UTF8.GetBytes(json, 0, json.Length, bytes, bom.Length);
        File.WriteAllBytes(tempFile, bytes);

        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "json", new() { Flatten = true }).ToList();
            Assert.Single(result);
            Assert.Equal(1L, result[0]["Id"]);
            Assert.Equal("Alice", result[0]["Name"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseJsonl_ValidLines_ParsesCorrectly()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string jsonl = "{\"Id\": 1, \"Name\": \"Alice\"}\n{\"Id\": 2, \"Name\": \"Bob\"}";
        File.WriteAllText(tempFile, jsonl);

        try
        {
            // Act
            var result = FileParsers.ParseByFormat(tempFile, "jsonl", new() { Flatten = true }).ToList();

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal(1L, result[0]["Id"]);
            Assert.Equal("Alice", result[0]["Name"]);
            Assert.Equal(2L, result[1]["Id"]);
            Assert.Equal("Bob", result[1]["Name"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseCsv_ValidRows_ParsesCorrectly()
    {
        // Arrange
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        string csv = "Id,Name,Age\r\n1,Alice,30\r\n2,Bob,25";
        File.WriteAllText(tempFile, csv);

        try
        {
            // Act
            var result = FileParsers.ParseByFormat(tempFile, "csv", new()).ToList();

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("1", result[0]["Id"]?.ToString());
            Assert.Equal("Alice", result[0]["Name"]);
            Assert.Equal("30", result[0]["Age"]?.ToString());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseXml_ValidXml_ParsesCorrectly()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string xml = @"<root>
            <item><Id>1</Id><Name>Alice</Name></item>
            <item><Id>2</Id><Name>Bob</Name></item>
        </root>";
        File.WriteAllText(tempFile, xml);

        try
        {
            // Act
            var result = FileParsers.ParseByFormat(tempFile, "xml", new()).ToList();

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("1", result[0]["Id"]);
            Assert.Equal("Alice", result[0]["Name"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseYaml_ValidYaml_ParsesCorrectly()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string yaml = @"
- Id: 1
  Name: Alice
- Id: 2
  Name: Bob
";
        File.WriteAllText(tempFile, yaml);

        try
        {
            // Act
            var result = FileParsers.ParseByFormat(tempFile, "yaml", new()).ToList();

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("1", result[0]["Id"]?.ToString());
            Assert.Equal("Alice", result[0]["Name"]?.ToString());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
