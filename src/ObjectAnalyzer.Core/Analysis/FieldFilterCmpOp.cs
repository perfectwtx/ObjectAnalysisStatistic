using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

internal enum FieldFilterCmpOp
{
    Equal, NotEqual, Greater, Less, GreaterEqual, LessEqual,
    In, NotIn, Like, NotLike
}
