namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 个人敏感信息 (PII) 分类。
/// </summary>
public enum PiiCategory
{
    None,
    Phone,
    IdCard,
    BankCard,
    Name,
    Email,
    /// <summary>美国社会安全号（SSN，ddd-dd-dddd）。</summary>
    Ssn,
    /// <summary>国际银行账号（IBAN，MOD 97 校验通过）。</summary>
    Iban,
    /// <summary>护照号（字段名提示 + 值形态）。</summary>
    Passport,
    /// <summary>自定义业务敏感字段（由规则文件 field.pii 声明），打码格式由规则指定。</summary>
    Custom
}
