using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

// ──────────────────────────────────────────────
// 词法分析
// ──────────────────────────────────────────────

internal enum FieldFilterTokenKind
{
    Identifier, Number, String, True, False, Null,
    OpEqual, OpNotEqual, OpGreater, OpLess, OpGreaterEqual, OpLessEqual,
    In, Not, Like, And, Or,
    LParen, RParen, LBracket, RBracket, Comma, End
}
