using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObjectAnalyzer;

public static class TypeLoader
{
    /// <summary>
    /// 编译指定的 .cs 文件，并返回编译后的 Assembly 和目标 Type。
    /// </summary>
    /// <param name="csFilePath">.cs 源文件路径</param>
    /// <param name="typeName">目标类名（精确匹配）。为空时取唯一 public class。</param>
    /// <returns>(编译后的 Assembly, 目标 Type)</returns>
    public static (Assembly assembly, Type type) Compile(string csFilePath, string? typeName)
    {
        if (!File.Exists(csFilePath))
            throw new FileNotFoundException($"类型定义文件不存在: {csFilePath}");

        string sourceCode = File.ReadAllText(csFilePath);

        // 解析语法树
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: csFilePath);

        // 收集运行时程序集引用（确保能编译基础类型）
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
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"  {d.GetMessage()} ({d.Location.GetLineSpan()})");
            throw new InvalidOperationException(
                $"编译 .cs 文件失败，共 {emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} 个错误:\n" +
                string.Join("\n", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());

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
                throw new InvalidOperationException(
                    $" .cs 文件中包含多个 public class: {names}。请使用 --type-name 指定目标类名。");
            }
        }

        if (targetType == null)
        {
            var allTypes = assembly.GetTypes().Where(t => t.IsClass).Select(t => t.Name);
            throw new InvalidOperationException(
                $"未在编译结果中找到类 '{typeName}'。可用的类: {string.Join(", ", allTypes)}");
        }

        return (assembly, targetType);
    }

    private static List<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();

        // 添加核心运行时程序集
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? Array.Empty<string>();

        foreach (var path in trustedAssemblies)
        {
            if (File.Exists(path))
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
