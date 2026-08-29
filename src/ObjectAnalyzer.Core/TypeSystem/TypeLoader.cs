using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObjectAnalyzer.TypeSystem;

public static class TypeLoader
{
    /// <summary>
    /// 编译指定的 .cs 文件，并返回编译后的 AssemblyLoadContext 和目标 Type。
    /// </summary>
    /// <param name="csFilePath">.cs 源文件路径</param>
    /// <param name="typeName">目标类名（精确匹配）。为空时取唯一 public class。</param>
    /// <returns>(编译后的 DynamicAssemblyContext, 目标 Type)</returns>
    public static (DynamicAssemblyContext context, Type type) Compile(string csFilePath, string? typeName)
    {
        if (!File.Exists(csFilePath))
            throw new FileNotFoundException($"类型定义文件不存在: {csFilePath}");

        string sourceCode = File.ReadAllText(csFilePath);

        // 解析语法树
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: csFilePath);

        // 收集运行时程序集引用（优先使用核心白名单，编译缺少引用时回退到全部 TPA）
        var references = GetMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DynamicType_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // 编译并检查错误
        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            // 若是因为引用不完整导致失败（如用户类型使用了白名单之外的程序集），回退到完整 TPA 重新编译
            if (emitResult.Diagnostics.Any(d =>
                    d.Severity == DiagnosticSeverity.Error &&
                    (d.Id is "CS0234" or "CS0246" or "CS0012" or "CS0400" or "CS0616")))
            {
                references = GetFullMetadataReferences();
                lock (_referenceSetLock)
                {
                    _usedFullReferenceSet = true;
                }
                compilation = compilation.WithReferences(references);
                ms.SetLength(0);
                ms.Position = 0;
                emitResult = compilation.Emit(ms);
            }

            if (!emitResult.Success)
            {
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"  {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)} ({d.Location.GetLineSpan()})");
                throw new InvalidOperationException(
                    $"编译 .cs 文件失败，共 {emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} 个错误:\n" +
                    string.Join("\n", errors));
            }
        }

        ms.Seek(0, SeekOrigin.Begin);
        
        var context = new DynamicAssemblyContext();
        Assembly assembly;
        try
        {
            assembly = context.LoadFromStream(ms);
        }
        catch (Exception ex)
        {
            context.Dispose();
            throw new InvalidOperationException($"将编译生成的程序集加载到沙盒上下文失败: {ex.Message}", ex);
        }

        // 查找目标类型
        Type? targetType = null;

        if (!string.IsNullOrEmpty(typeName))
        {
            // 按类名精确匹配（支持不带命名空间的短名匹配）
            targetType = assembly.GetTypes()
                .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
        }
        else
        {
            // 取唯一的 public class
            var publicClasses = assembly.GetTypes()
                .Where(t => t.IsClass && t.IsPublic)
                .ToList();

            if (publicClasses.Count == 1)
            {
                targetType = publicClasses[0];
            }
            else if (publicClasses.Count > 1)
            {
                var names = string.Join(", ", publicClasses.Select(t => t.Name));
                context.Dispose();
                throw new InvalidOperationException(
                    $" .cs 文件中包含多个 public class: {names}。请在 --type 中指定类名，如 --type \"UserInfo.cs:User\"。");
            }
        }

        if (targetType == null)
        {
            var allTypes = assembly.GetTypes().Where(t => t.IsClass).Select(t => t.Name);
            context.Dispose();
            throw new InvalidOperationException(
                $"未在编译结果中找到类 '{typeName}'。可用的类: {string.Join(", ", allTypes)}");
        }

        return (context, targetType);
    }

    // 大多数 .cs 类型定义只依赖这些核心程序集，优先加载它们以避免数百个 TPA 的冷启动开销
    private static readonly string[] _coreReferenceNames =
    {
        "netstandard",
        "System.Runtime",
        "System.Private.CoreLib",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Collections.Specialized",
        "System.Linq",
        "System.Linq.Expressions",
        "System.Text.Json",
        "System.Text.Encoding",
        "System.Text.Encoding.Extensions",
        "System.Text.RegularExpressions",
        "System.IO",
        "System.IO.FileSystem",
        "System.Globalization",
        "System.Reflection",
        "System.Reflection.Primitives",
        "System.Runtime.InteropServices",
        "System.Runtime.Extensions",
        "System.Threading",
        "System.Threading.Tasks",
        "System.ComponentModel",
        "System.ComponentModel.Primitives",
        "System.ComponentModel.TypeConverter",
        "System.Data.Common",
        "System.Xml.XmlDocument",
        "System.Xml.ReaderWriter",
        "System.Net.Http",
        "System.Security.Cryptography",
        "System.Formats.Asn1",
        "System.ObjectModel",
        "System.Memory",
        "System.Numerics.Vectors"
    };

    // 标记当前进程是否已使用完整 TPA 引用集（用于回退判定与缓存）。
    // 并发 Compile（Blazor 多电路）下用锁保护，避免引用集在编译中途切换。
    private static readonly object _referenceSetLock = new();
    private static bool _usedFullReferenceSet;

    // TPA 路径索引（程序集名 → 完整路径），进程内只构建一次
    private static readonly Lazy<Dictionary<string, string>> _tpaIndex = new(BuildTpaIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    private static List<MetadataReference> GetMetadataReferences()
    {
        lock (_referenceSetLock)
        {
            return _usedFullReferenceSet
                ? GetFullMetadataReferences()
                : LoadReferencesByName(_coreReferenceNames);
        }
    }

    private static Dictionary<string, string> BuildTpaIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? Array.Empty<string>();

        foreach (var path in trustedAssemblies)
        {
            if (!File.Exists(path)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (name != null) index.TryAdd(name, path);
        }
        return index;
    }

    private static List<MetadataReference> GetFullMetadataReferences()
    {
        var references = new List<MetadataReference>();

        foreach (var path in _tpaIndex.Value.Values)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // 跳过无法加载的程序集
            }
        }

        return references;
    }

    private static List<MetadataReference> LoadReferencesByName(IReadOnlyCollection<string> assemblyNames)
    {
        var references = new List<MetadataReference>();

        foreach (var name in assemblyNames)
        {
            if (_tpaIndex.Value.TryGetValue(name, out var path))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // 跳过无法加载的程序集
                }
            }
        }

        return references;
    }
}
