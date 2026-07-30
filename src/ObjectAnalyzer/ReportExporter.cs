using System;
using System.IO;
using System.Text.Json;

namespace ObjectAnalyzer;

public static class ReportExporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Save(AnalysisResult result, string outputPath)
    {
        try
        {
            Console.WriteLine($"正在将详细分析报告保存至: {outputPath} ...");
            string jsonString = JsonSerializer.Serialize(result, SerializerOptions);
            File.WriteAllText(outputPath, jsonString);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"报告成功保存！");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"保存报告时出错: {ex.Message}");
            Console.ResetColor();
        }
    }
}
