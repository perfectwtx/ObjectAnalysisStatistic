using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

internal readonly struct FieldFilterToken
{
    public FieldFilterToken(FieldFilterTokenKind kind, string? text = null, double? number = null)
    {
        Kind = kind;
        Text = text;
        Number = number;
    }

    public FieldFilterTokenKind Kind { get; }
    public string? Text { get; }
    public double? Number { get; }
}
