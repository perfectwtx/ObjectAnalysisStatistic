
namespace ObjectAnalyzer.Security;

/// <summary>
/// 在规则加载完成后遍历所有字段的 transform，把其中以 <c>kms:</c> 前缀引用的
/// 敏感密钥（key / iv / privateKey / publicKey / signature）解密为明文，
/// 使下游解码管线（FieldTransformProcessor 及 DecodeBytes）完全无感知地拿到真实密钥。
///
/// 由于解密发生在内存中的规则模型上，规则 JSON 本身永远不会被改写为明文，
/// 明文字符串仅存活于本次进程的分析周期内。
/// </summary>
public static class KmsRuleResolver
{
    /// <summary>需尝试 KMS 解析的 transform 敏感字段名。</summary>
    private static readonly string[] SensitiveFields = { "key", "iv", "privateKey", "publicKey", "signature" };

    /// <summary>
    /// 就地解析规则中所有 KMS 密文引用；无 KMS 引用时原样返回（零开销）。
    /// </summary>
    public static void Resolve(AnalysisRules rules, IAnalysisConsole? console = null)
    {
        if (rules?.Fields == null)
            return;

        bool anyResolved = false;
        foreach (var field in rules.Fields)
        {
            var transform = field.Value?.Transform;
            if (transform == null)
                continue;

            foreach (string sensitive in SensitiveFields)
            {
                string? resolved = ResolveField(transform, sensitive);
                if (resolved != null && resolved != GetField(transform, sensitive))
                {
                    SetField(transform, sensitive, resolved);
                    anyResolved = true;
                }
            }
        }

        if (anyResolved)
        {
            (console ?? SilentConsole.Instance).MarkupLine("[grey][[KMS]][/] 已从 KMS 密文引用解密敏感密钥（明文未写入规则文件）。");
        }
    }

    private static string? ResolveField(FieldTransform t, string field) => field switch
    {
        "key" => KmsSecretResolver.Resolve(t.Key),
        "iv" => KmsSecretResolver.Resolve(t.Iv),
        "privateKey" => KmsSecretResolver.Resolve(t.PrivateKey),
        "publicKey" => KmsSecretResolver.Resolve(t.PublicKey),
        "signature" => KmsSecretResolver.Resolve(t.Signature),
        _ => null
    };

    private static string? GetField(FieldTransform t, string field) => field switch
    {
        "key" => t.Key,
        "iv" => t.Iv,
        "privateKey" => t.PrivateKey,
        "publicKey" => t.PublicKey,
        "signature" => t.Signature,
        _ => null
    };

    private static void SetField(FieldTransform t, string field, string value)
    {
        switch (field)
        {
            case "key": t.Key = value; break;
            case "iv": t.Iv = value; break;
            case "privateKey": t.PrivateKey = value; break;
            case "publicKey": t.PublicKey = value; break;
            case "signature": t.Signature = value; break;
        }
    }
}
