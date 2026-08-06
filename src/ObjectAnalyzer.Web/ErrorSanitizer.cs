using System.IO;
using System.Text.Json;

namespace ObjectAnalyzer.Web;

/// <summary>
/// Web 界面错误信息脱敏：避免向用户泄露服务器内部路径等敏感信息。
/// </summary>
public static class ErrorSanitizer
{
    private static readonly string TempPath = Path.GetTempPath();

    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        return message.Replace(TempPath, "<临时目录>", System.StringComparison.OrdinalIgnoreCase);
    }

    public static string Format(Exception ex)
    {
        string message = ex switch
        {
            FileNotFoundException or JsonException or InvalidDataException or InvalidOperationException
                => ex.Message,
            _ => "分析失败，请检查输入文件、上传内容或参数设置。"
        };
        return Sanitize(message);
    }
}
