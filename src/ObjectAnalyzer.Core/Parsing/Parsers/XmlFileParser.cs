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

internal sealed class XmlFileParser : IFileParser
{
    public IReadOnlyList<string> SupportedFormats => new[] { "xml" };
    public IReadOnlyList<string> SupportedExtensions => new[] { ".xml" };

    public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
    {
        XDocument doc;
        try
        {
            // 显式禁止 DTD（防 XXE/实体炸弹）并限制文档大小，防止超大/恶意 XML 打满内存
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 256 * 1024 * 1024
            };
            using var xmlReader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(xmlReader);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"解析 XML 文件时出错: {ex.Message}", ex);
        }

        if (doc.Root == null)
            throw new InvalidDataException("XML 文件缺少根节点。");

        long skip = Math.Max(0, options.StartFromLine);
        long dataIndex = 0;
        foreach (var element in doc.Root.Elements())
        {
            if (dataIndex < skip)
            {
                dataIndex++;
                continue; // P6.2：跳过前 StartFromLine 个根子元素
            }
            yield return ParserHelpers.ConvertXElement(element, 0);
        }
    }
}
