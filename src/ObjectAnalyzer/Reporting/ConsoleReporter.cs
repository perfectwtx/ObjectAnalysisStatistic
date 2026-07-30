using System;
using System.Linq;

namespace ObjectAnalyzer;

public static class ConsoleReporter
{
    public static void Print(AnalysisResult result, int maxValuesToShow)
    {
        Console.WriteLine("================================================================================");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"                      分析摘要报告 (总对象数: {result.TotalObjects})");
        Console.ResetColor();
        Console.WriteLine("================================================================================");

        // 如果存在重复对象，打印重复对象统计
        if (result.DuplicateObjects.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[重复对象统计] 发现 {result.DuplicateObjects.Count} 组重复出现的对象 (不同对象数: {result.TotalUniqueObjects})");
            Console.ResetColor();
            foreach (var dup in result.DuplicateObjects.Take(3)) // 控制台最多显示 3 组
            {
                Console.WriteLine($"  • 重复出现 {dup.Count} 次: {dup.ObjectJson}");
            }
            if (result.DuplicateObjects.Count > 3)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ... 还有 {result.DuplicateObjects.Count - 3} 组重复对象未列出");
                Console.ResetColor();
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
        }

        foreach (var stat in result.FieldStatistics)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"字段: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{stat.FieldName,-35}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($" 频次: ");
            Console.ResetColor();
            Console.Write($"{stat.Count}/{result.TotalObjects} ({stat.Coverage * 100:F1}%)");
            Console.WriteLine();

            // 打印类型统计与推断的默认值说明
            string defaultValInfo = stat.DefaultValueRepresentation != null ? $" (推断默认值: {stat.DefaultValueRepresentation})" : "";
            string typesStr = string.Join(", ", stat.TypeCounts.Select(t => $"{t.Key}({t.Value})"));
            Console.WriteLine($"  ├─ 数据类型: {typesStr}{defaultValInfo}");

            // 打印值频率
            Console.WriteLine($"  └─ 唯一值频次 (最多展示前 {maxValuesToShow} 个):");
            var sortedValues = stat.ValueCounts.OrderByDescending(v => v.Value).ToList();
            int shownCount = 0;
            foreach (var val in sortedValues)
            {
                if (shownCount >= maxValuesToShow)
                {
                    int remaining = sortedValues.Count - maxValuesToShow;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"     ... 还有 {remaining} 个唯一值已省略");
                    Console.ResetColor();
                    break;
                }

                string displayVal = val.Key;
                if (displayVal.Length > 50)
                {
                    displayVal = string.Concat(displayVal.AsSpan(0, 47), "...");
                }
                displayVal = displayVal.Replace("\r", "\\r").Replace("\n", "\\n");

                // 判断是否是该字段推断出的默认值
                bool isDefaultValue = stat.DefaultValueRepresentation != null &&
                    string.Equals(val.Key, stat.DefaultValueRepresentation, StringComparison.OrdinalIgnoreCase);

                string defaultTag = isDefaultValue ? " [默认值]" : "";

                double valPercentage = stat.Count > 0 ? (double)val.Value / stat.Count : 0;
                Console.Write($"     • [{(valPercentage * 100):F1}%] \"{displayVal}\" -> 出现 {val.Value} 次");
                if (isDefaultValue)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(defaultTag);
                    Console.ResetColor();
                }
                Console.WriteLine();
                shownCount++;
            }
            Console.WriteLine();
        }
        Console.WriteLine("================================================================================");
    }
}
