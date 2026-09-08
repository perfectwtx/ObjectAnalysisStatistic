namespace ObjectAnalyzer.Tests;

/// <summary>
/// CompressionDetector 单元测试：覆盖魔数嗅探与扩展名分支的边界情况，
/// 并验证 Detect 对空 / null 输入的防御性兜底（避免上游把 null 传进来时
/// 触发 NRE —— 这条路径上以前出过闭包捕获 race condition，详见
/// AsyncAnalyzeEndpoints 的 tempData 复制修复）。
/// </summary>
public class CompressionDetectorTests
{
    [Fact]
    [Trait("Category", "CompressionDetector")]
    public void Detect_NullPath_ReturnsNone()
    {
        // 兜底契约：上游（异步任务、反射、第三方代码）可能把 null 塞进来，
        // 此时必须静默回退到 None，而不是抛 NullReferenceException。
        var result = CompressionDetector.Detect(null!);
        Assert.Equal(CompressionType.None, result);
    }

    [Fact]
    [Trait("Category", "CompressionDetector")]
    public void Detect_EmptyPath_ReturnsNone()
    {
        var result = CompressionDetector.Detect("");
        Assert.Equal(CompressionType.None, result);
    }

    [Fact]
    [Trait("Category", "CompressionDetector")]
    public void Detect_WhitespacePath_DoesNotThrow()
    {
        // Path.GetExtension("   ") 返回 ""，不抛 NRE —— 这里走默认分支最终 SniffByMagic
        // 因 File.Exists==false 而返回 None，全流程不应抛任何异常。
        var ex = Record.Exception(() => CompressionDetector.Detect("   "));
        Assert.Null(ex);
    }

    [Fact]
    [Trait("Category", "CompressionDetector")]
    public void Detect_KnownCompressionExtensions_ReturnType()
    {
        // 扩展名优先级：.gz / .bz2 / .zip 在任何文件存在与否之前就已确定。
        // 这些用例故意指向不存在的路径，确保不会被 SniffByMagic 覆盖判定。
        Assert.Equal(CompressionType.GZip, CompressionDetector.Detect("/tmp/data.json.gz"));
        Assert.Equal(CompressionType.BZip2, CompressionDetector.Detect("/tmp/data.json.bz2"));
        Assert.Equal(CompressionType.Zip, CompressionDetector.Detect("/tmp/data.zip"));
    }

    [Fact]
    [Trait("Category", "CompressionDetector")]
    public void Detect_KnownDataExtension_NotInferredAsZip()
    {
        // 已知数据扩展名（如 .csv）即便文件实际不存在，也不应被错判为 None 之外的类型。
        // 这里依赖 SniffByMagic 在文件不存在时返回 None；只验证路径不会抛 / 不会返回 Zip。
        var result = CompressionDetector.Detect("/nonexistent/data.csv");
        Assert.Equal(CompressionType.None, result);
    }
}
