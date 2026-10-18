namespace ObjectAnalyzer.Security;

/// <summary>
/// 解析 <c>kms:</c> 前缀并按 provider 路由到对应 <see cref="IKmsProvider"/> 解密密文。
///
/// 密文引用格式：<c>kms:&lt;provider&gt;/&lt;version&gt;:&lt;base64 payload&gt;</c>
/// 版本 <c>version</c> 由各实现自定义用途（offline 用于声明 payload 编码：utf8 / base64 / hex）。
/// 非 <c>kms:</c> 前缀的普通字符串原样返回。
/// </summary>
public static class KmsSecretResolver
{
    public const string Prefix = "kms:";

    private static readonly Dictionary<string, IKmsProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    static KmsSecretResolver()
    {
        Register(new OfflineKmsProvider());
    }

    /// <summary>注册一个提供方；同名重复注册以后者覆盖。</summary>
    public static void Register(IKmsProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ProviderName))
            throw new ArgumentException("KMS provider 名称不能为空。", nameof(provider));
        _providers[provider.ProviderName] = provider;
    }

    /// <summary>判断一个值是否为 KMS 密文引用（以 <c>kms:</c> 开头）。</summary>
    public static bool IsKmsReference(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 若 <paramref name="value"/> 为 KMS 密文引用，则解密并按其编码还原为字符串；
    /// 否则原样返回。
    /// payload 编码：若 payload 本身是 Base64 文本密钥（以等号结尾或可判为 Base64）按原样解码为字符串，
    /// 否则按 UTF8 处理。交由解密结果的语义由生产方控制——offline 实现内联携带编码标记。
    /// </summary>
    public static string Resolve(string? value)
    {
        if (!IsKmsReference(value))
            return value ?? string.Empty;

        if (!TryParse(value!, out string provider, out _, out string payload))
            throw new InvalidDataException($"KMS 密文引用格式无效: {value}");

        if (!_providers.TryGetValue(provider, out var impl))
            throw new InvalidDataException($"未知的 KMS 提供方 '{provider}'（已注册: {string.Join(", ", _providers.Keys)}）。");

        byte[] secret = impl.Decrypt(payload);
        return DecodeSecret(secret, value!);
    }

    /// <summary>
    /// 将 KMS 解出的明文密钥字节还原为字符串：以 UTF8 表示；由 offline 实现把
    /// “目标格式”（utf8 / base64 / hex）写进版本段，这里按约定还原。见 <see cref="OfflineKmsProvider"/>。
    /// 其它 provider 默认按 UTF8 文本解码（生产方应返回可直接使用的明文）。
    /// </summary>
    private static string DecodeSecret(byte[] secret, string reference)
    {
        // 解析出 provider 与 version，交给各实现语义（offline 在 version 中携带编码标记）。
        if (TryParse(reference, out string provider, out string version, out _))
        {
            if (provider == OfflineKmsProvider.ProviderName &&
                version.StartsWith("1-", StringComparison.Ordinal))
            {
                string encoding = version[2..];
                return encoding.ToLowerInvariant() switch
                {
                    "base64" => Convert.ToBase64String(secret),
                    "hex" => Convert.ToHexString(secret),
                    _ => System.Text.Encoding.UTF8.GetString(secret)
                };
            }
        }

        return System.Text.Encoding.UTF8.GetString(secret);
    }

    /// <summary>把一个值中的所有 KMS 引用替换路径（供外部遍历多字段时复用）。</summary>
    internal static bool TryParse(string reference, out string provider, out string version, out string payload)
    {
        provider = "";
        version = "";
        payload = "";

        if (!IsKmsReference(reference))
            return false;

        // 形如 "kms:offline/1-base64:U2FsdG..."；先取首个 ':' 之后的部分作为 provider 段
        string rest = reference[Prefix.Length..];
        int versionColon = rest.IndexOf(':');
        if (versionColon < 0)
            return false;

        string providerSegment = rest[..versionColon];
        payload = rest[(versionColon + 1)..];

        int slash = providerSegment.IndexOf('/');
        if (slash < 0)
        {
            provider = providerSegment;
            version = "";
        }
        else
        {
            provider = providerSegment[..slash];
            version = providerSegment[(slash + 1)..];
        }

        return provider.Length > 0 && payload.Length > 0;
    }
}
