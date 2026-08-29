using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MiniExcelLibs;
using ObjectAnalyzer.Analysis;
using YamlDotNet.Serialization;

namespace ObjectAnalyzer.Parsing.Parsers;

/// <summary>
/// 惰性枚举包装：在迭代开始（GetEnumerator）时才打开压缩输入源，
/// 并在外层枚举器释放时一并释放解压流与底层句柄，确保文件句柄不泄漏。
/// </summary>
internal sealed class DecompressingEnumerable : IEnumerable<Dictionary<string, object?>>
{
    private readonly string _path;
    private readonly IFileParser _parser;
    private readonly ParseOptions _options;

    public DecompressingEnumerable(string path, IFileParser parser, ParseOptions options)
    {
        _path = path;
        _parser = parser;
        _options = options;
    }

    public IEnumerator<Dictionary<string, object?>> GetEnumerator()
    {
        var source = InputSource.Open(_path);
        try
        {
            var inner = _parser.Parse(source.Content, _options).GetEnumerator();
            return new DisposingEnumerator(inner, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class DisposingEnumerator : IEnumerator<Dictionary<string, object?>>
    {
        private readonly IEnumerator<Dictionary<string, object?>> _inner;
        private readonly InputSource _source;

        public DisposingEnumerator(IEnumerator<Dictionary<string, object?>> inner, InputSource source)
        {
            _inner = inner;
            _source = source;
        }

        public Dictionary<string, object?> Current => _inner.Current;
        object System.Collections.IEnumerator.Current => _inner.Current;
        public bool MoveNext() => _inner.MoveNext();
        public void Reset() => _inner.Reset();
        public void Dispose()
        {
            _inner.Dispose();
            _source.Dispose();
        }
    }
}
