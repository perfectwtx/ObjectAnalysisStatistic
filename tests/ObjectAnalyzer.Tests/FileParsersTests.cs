using MiniExcelLibs;

namespace ObjectAnalyzer.Tests;

public class FileParsersTests
{
    [Fact]
    public void ParseCsv_HeaderModes_RespectConfig()
    {
        // header=1（默认）首行为表头；header=0 无表头——首行即数据、列键用字母
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(tempFile, "Name,Age,City\r\nAlice,30,Beijing\r\nBob,25,Shanghai\r\n");
        try
        {
            var byName = FileParsers.ParseByFormat(tempFile, "csv", new ParseOptions
            {
                ValueParsers = new[] { new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "Name", ParseType = "auto", Header = 1 } }
            }).ToList();
            Assert.Equal(2, byName.Count);
            Assert.Equal("Alice", byName[0]["Name"]);

            // header=0：无表头语义——首行即数据，列键用列字母（原表头行本身成为一条数据）
            var byLetter = FileParsers.ParseByFormat(tempFile, "csv", new ParseOptions
            {
                ValueParsers = new[] { new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "B", ParseType = "auto", Header = 0 } }
            }).ToList();
            Assert.Equal(3, byLetter.Count);
            Assert.Equal("Age", byLetter[0].GetValueOrDefault("B"));
            // CSV 会推断数值类型："30" → 30L（CSV 无类型信息，不推断则数值列
            // 的均值/分位数/相关性全部失效）。保留字符串需传 CsvInferNumbers = false
            Assert.Equal(30L, byLetter[1].GetValueOrDefault("B"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
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

   [Fact]
   public void DetectFormat_Xlsx_UsesExcelBranch()
   {
       string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".xlsx");
       Assert.Equal("excel", FileParsers.DetectFormat(tempFile));
   }

   [Fact]
   public void ParseExcelJson_MultipleColumns_PreservesSeparateBuckets()
   {
       string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".xlsx");
       var payload = new[]
       {
           new Dictionary<string, object?>
           {
               { "A", "{\"id\":1,\"name\":\"Alice\"}" },
               { "B", "{\"age\":30,\"city\":\"Beijing\"}" },
               { "C", "{\"role\":\"Admin\"}" }
           }
       };

       // printHeader:false：无表头表——首行即数据，配合 header=0（列键为字母）锁定新语义
       MiniExcel.SaveAs(tempFile, payload, printHeader: false);

       try
       {
           var result = FileParsers.ParseByFormat(tempFile, "excel-json", new ParseOptions
           {
               Flatten = true,
               ValueParsers = new[]
               {
                   new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "A", ParseType = "json", Header = 0 },
                   new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "B", ParseType = "json", Header = 0 },
                   new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "C", ParseType = "json", Header = 0 }
               }
           }).ToList();

           Assert.Single(result);
           Assert.Equal(1L, result[0]["A.id"]);
           Assert.Equal("Alice", result[0]["A.name"]);
           Assert.Equal(30L, result[0]["B.age"]);
           Assert.Equal("Beijing", result[0]["B.city"]);
           Assert.Equal("Admin", result[0]["C.role"]);
           Assert.False(result[0].ContainsKey("A"));
           Assert.False(result[0].ContainsKey("B"));
           Assert.False(result[0].ContainsKey("C"));
       }
       finally
       {
           if (File.Exists(tempFile)) File.Delete(tempFile);
       }
   }

   [Fact]
   public void ParseExcelJson_PerSourceFlattenOverride_Works()
   {
       string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".xlsx");
       var payload = new[]
       {
           new Dictionary<string, object?>
           {
               { "A", "{\"id\":1,\"name\":\"Alice\"}" },
               { "B", "{\"age\":30,\"city\":\"Beijing\"}" }
           }
       };

       MiniExcel.SaveAs(tempFile, payload, printHeader: false);

       try
       {
           var result = FileParsers.ParseByFormat(tempFile, "excel-json", new ParseOptions
           {
               Flatten = false,
               ValueParsers = new[]
               {
                   new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "A", ParseType = "json", Flatten = true, Header = 0 },
                   new ObjectAnalyzer.Analysis.ValueParserConfig { Source = "B", ParseType = "json", Flatten = false, Header = 0 }
               }
           }).ToList();

           Assert.Single(result);
           Assert.Equal(1L, result[0]["A.id"]);
           Assert.Equal("Alice", result[0]["A.name"]);
           var b = Assert.IsType<Dictionary<string, object?>>(result[0]["B"]);
           Assert.Equal(30L, b["age"]);
           Assert.Equal("Beijing", b["city"]);
       }
       finally
       {
           if (File.Exists(tempFile)) File.Delete(tempFile);
       }
   }
}
