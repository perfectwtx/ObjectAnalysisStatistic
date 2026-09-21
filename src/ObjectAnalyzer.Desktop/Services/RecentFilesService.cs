using System.IO;
using System.Text.Json;

namespace ObjectAnalyzer.Desktop.Services;

public static class RecentFilesService
{
    private const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private static string StoragePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "recent-files.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(StoragePath))
                return [];

            var paths = JsonSerializer.Deserialize<List<string>>(
                File.ReadAllText(StoragePath),
                s_jsonOptions);

            return paths?
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentFiles)
                .ToArray()
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            string directory = Path.GetDirectoryName(StoragePath)
                ?? throw new InvalidOperationException("无法确定最近文件存储目录。");

            Directory.CreateDirectory(directory);

            var normalized = paths
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentFiles)
                .ToArray();

            File.WriteAllText(
                StoragePath,
                JsonSerializer.Serialize(normalized, s_jsonOptions));
        }
        catch
        {
            // 最近文件属于辅助信息，保存失败不影响分析主流程。
        }
    }
}
