using MiniExcelLibs;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Generates sample Excel test files for development and demo purposes.
/// Invoked via --generate-test-excel CLI option.
/// </summary>
public static class TestFileGenerator
{
    public static void GenerateAll()
    {
        GenerateTestExcel();
        GenerateTestExcelJson();
        GenerateTestExcelJsonMultiColumns();
    }

    private static void GenerateTestExcel()
    {
        try
        {
            var data = new[] {
                new Dictionary<string, object?> { { "Id", 1 }, { "Name", "张三" }, { "Age", 28 }, { "Role", "Admin" } },
                new Dictionary<string, object?> { { "Id", 2 }, { "Name", "李四" }, { "Age", null }, { "Role", "User" } },
                new Dictionary<string, object?> { { "Id", 3 }, { "Name", "王五" }, { "Age", 25 }, { "Role", null } }
            };
            Directory.CreateDirectory("samples");
            SaveAsOverwrite("samples/test_data.xlsx", data);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("成功生成 Excel 测试文件: samples/test_data.xlsx");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"生成 Excel 测试文件失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void GenerateTestExcelJson()
    {
        try
        {
            // A 列每行存储一个 JSON 对象字符串
            var data = new[] {
                new Dictionary<string, object?> { { "A", "{\"id\":1,\"name\":\"张三\",\"age\":28,\"gender\":\"Male\",\"city\":\"北京\"}" } },
                new Dictionary<string, object?> { { "A", "{\"id\":2,\"name\":\"李四\",\"age\":32,\"gender\":\"Male\",\"city\":\"上海\"}" } },
                new Dictionary<string, object?> { { "A", "{\"id\":3,\"name\":\"王五\",\"age\":25,\"gender\":\"Female\",\"city\":\"北京\"}" } }
            };
            Directory.CreateDirectory("samples");
            SaveAsOverwrite("samples/test_data_excel_json.xlsx", data);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("成功生成 Excel JSON 测试文件: samples/test_data_excel_json.xlsx");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"生成 Excel JSON 测试文件失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void GenerateTestExcelJsonMultiColumns()
    {
        try
        {
            var data = new[] {
                new Dictionary<string, object?>
                {
                    { "A", "{\"id\":1,\"name\":\"张三\"}" },
                    { "B", "{\"age\":28,\"city\":\"北京\"}" },
                    { "C", "{\"role\":\"Admin\",\"status\":\"active\"}" }
                },
                new Dictionary<string, object?>
                {
                    { "A", "{\"id\":2,\"name\":\"李四\"}" },
                    { "B", "{\"age\":32,\"city\":\"上海\"}" },
                    { "C", "{\"role\":\"User\",\"status\":\"vip\"}" }
                },
                new Dictionary<string, object?>
                {
                    { "A", "{\"id\":3,\"name\":\"王五\"}" },
                    { "B", "{\"age\":25,\"city\":\"广州\"}" },
                    { "C", "{\"role\":\"Guest\",\"status\":\"inactive\"}" }
                }
            };

            Directory.CreateDirectory("samples");
            SaveAsOverwrite("samples/test_data_excel_json_multi_columns.xlsx", data);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("成功生成多列 Excel JSON 测试文件: samples/test_data_excel_json_multi_columns.xlsx");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"生成多列 Excel JSON 测试文件失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void SaveAsOverwrite(string path, object value)
    {
        if (File.Exists(path))
            File.Delete(path);

        MiniExcel.SaveAs(path, value);
    }
}
