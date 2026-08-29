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

internal sealed class YamlFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "yaml" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml" };

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        var deserializer = new DeserializerBuilder().Build();

        using var reader = new StreamReader(stream);
        object? yamlResult = deserializer.Deserialize(reader);

        if (yamlResult is List<object?> list)
        {
            long skip = Math.Max(0, options.StartFromLine);
            long dataIndex = 0;
            foreach (var item in list)
            {
                if (dataIndex < skip)
                {
                    dataIndex++;
                    continue; // P6.2：跳过前 StartFromLine 个列表元素
                }
                if (item is Dictionary<object, object> dict)
                {
                    var converted = ParserHelpers.ConvertYamlDict(dict, 0);
                    // Fix #4: respect options.Flatten for YAML (previously ignored)
                    yield return options.Flatten ? ParserHelpers.FlattenObjectDict(converted) : converted;
                }
            }
        }
        else if (yamlResult is Dictionary<object, object> singleDict)
        {
            var converted = ParserHelpers.ConvertYamlDict(singleDict, 0);
            yield return options.Flatten ? ParserHelpers.FlattenObjectDict(converted) : converted;
        }
        else
        {
            throw new InvalidDataException("YAML 根节点必须是对象列表或单个对象。");
        }
    }
}
