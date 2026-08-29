using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web;

/// <summary>
/// P6 选项在 Web 分析流程中的可测试拆解。
/// 把 <c>--resume</c> / <c>--baseline</c> / <c>--checkpoint</c> 三段后处理从 Home.razor 的实例方法
/// 拆到独立静态类，依赖仅通过入参与返回值传递——便于单元测试在不实例化 Blazor 组件的前提下
/// 直接覆盖路径穿越、合并正确性、错误返回等行为。
/// </summary>
internal static class P6PostAnalysis
{
    /// <summary>
    /// 把用户在 UI 上填写的检查点名称规整为安全的服务器文件名：
    /// 去路径分隔符、控制字符、跨平台禁用的非法字符；强制 .snapshot 后缀；
    /// 长度限制 240 字符（Windows 上限 255 留 15 字节缓冲）；全无效时回退为 <c>session-{guid}.snapshot</c>。
    ///
    /// 注意：非法字符集是「跨平台固定」的（涵盖 Windows 全部非法字符 + 正反斜杠 + 控制字符），
    /// 而不是依赖 <see cref="Path.GetInvalidFileNameChars"/>——后者随运行 OS 变化（Linux 上
    /// <c>&lt;&gt;:"|?*</c> 与反斜杠均合法），会导致同一输入在不同环境被清洗成不同结果，且会削弱
    /// 路径穿越防护的一致性。
    /// </summary>
    public static string SanitizeCheckpointName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (IsForbiddenNameChar(c)) sb.Append('_');
            else sb.Append(c);
        }
        var cleaned = sb.ToString();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return $"session-{Guid.NewGuid():N}.snapshot";
        }
        if (!cleaned.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase))
        {
            cleaned += ".snapshot";
        }
        if (cleaned.Length > 240)
        {
            // CA1845: 使用 string.Concat(span, ...) 比 Substring + + 更高效
            cleaned = string.Concat(cleaned.AsSpan(0, 240), ".snapshot");
        }
        return cleaned;
    }

    /// <summary>
    /// 判断字符是否属于跨平台禁用的检查点文件名字符：
    /// 控制字符（含 \0）、Windows 非法字符 <c>&lt; &gt; : " / \ | ? *</c>、以及空格本身也一并禁止
    /// （名称在清洗后空白会触发 GUID 兜底）。
    /// </summary>
    private static bool IsForbiddenNameChar(char c)
    {
        // 控制字符（0x00~0x1F 与 0x7F）
        if (c < 0x20 || c == 0x7F) return true;
        // Windows 文件系统非法字符 + 正反斜杠（路径分隔），跨平台统一清洗
        switch (c)
        {
            case '<':
            case '>':
            case ':':
            case '"':
            case '/':
            case '\\':
            case '|':
            case '?':
            case '*':
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 把 <c>--resume</c> / <c>--baseline</c> / <c>--checkpoint</c> 三种 P6 选项应用到 <see cref="P6PostAnalysisInput.Result"/>。
    /// 行为与 CLI 对齐，但 Web 端不跳读源文件位置（仅合并聚合结果）。
    ///
    /// 异常被吞掉并写到 <see cref="P6PostAnalysisResult"/> 的对应错误字段——
    /// 增量步骤失败不应阻塞本次主分析结果，由调用方（Home.razor）决定是否向用户展示。
    /// </summary>
    public static P6PostAnalysisResult Apply(P6PostAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = input.Result;
        var output = new P6PostAnalysisResult();

        // P6.2 断点续跑：把历史 AnalyzerSnapshot.Result 合并进当前 result
        if (!string.IsNullOrEmpty(input.ResumeFromPath) && File.Exists(input.ResumeFromPath))
        {
            try
            {
                var snapshot = AnalyzerSnapshot.LoadFrom(input.ResumeFromPath);
                result.MergeFrom(snapshot.Result);
            }
            catch (Exception ex)
            {
                output.ResumeErrorMessage = ex.Message;
            }
        }

        // P6.2 基线合并：把历史 AnalysisResult JSON 合并进当前 result
        if (!string.IsNullOrEmpty(input.BaselinePath) && File.Exists(input.BaselinePath))
        {
            try
            {
                var baselineJson = File.ReadAllText(input.BaselinePath);
                var baselineResult = System.Text.Json.JsonSerializer.Deserialize<AnalysisResult>(baselineJson);
                if (baselineResult != null)
                {
                    result.MergeFrom(baselineResult);
                }
            }
            catch (Exception ex)
            {
                output.BaselineErrorMessage = ex.Message;
            }
        }

        // P6.2 检查点落盘：把当前 result 写入 <CheckpointDirectory>/<CheckpointFileName>
        if (!string.IsNullOrEmpty(input.CheckpointFileName))
        {
            try
            {
                var dir = input.CheckpointDirectory
                    ?? Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints");
                Directory.CreateDirectory(dir);
                var fullPath = Path.Combine(dir, input.CheckpointFileName);
                var snapshot = new AnalyzerSnapshot
                {
                    SourcePath = input.SourcePath ?? "",
                    SourceFormat = input.SourceFormat ?? "",
                    Result = result
                };
                snapshot.SaveTo(fullPath);
                output.LastCheckpointServerPath = fullPath;
                output.LastCheckpointSize = new FileInfo(fullPath).Length;
            }
            catch (Exception ex)
            {
                output.CheckpointErrorMessage = ex.Message;
            }
        }

        return output;
    }
}
