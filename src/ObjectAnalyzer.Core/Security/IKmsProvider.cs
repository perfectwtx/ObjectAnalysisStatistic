using System;

namespace ObjectAnalyzer.Security;

/// <summary>
/// KMS（密钥托管服务）提供方抽象。用于在运行时把规则文件里以 <c>kms:</c> 前缀引用的
/// KMS 密文解密还原为明文密钥，从而避免把 AES/SM4/SM2 等解密密钥明文写进规则 JSON。
///
/// 规则文件里敏感字段的值形如：<c>kms:&lt;provider&gt;/&lt;version&gt;:&lt;payload&gt;</c>，
/// 例如 <c>kms:offline/1:U2FsdGVkX1+...</c>。<see cref="KmsSecretResolver"/> 按 provider 路由到
/// 对应实现，调用 <see cref="Decrypt"/> 还原明文。
///
/// 实现无需引入任何云 SDK：新增云厂商（AWS / 阿里云 / Azure Key Vault / 自建私有 KMS）
/// 只需实现本接口并在 <see cref="KmsSecretResolver"/> 中注册。
/// </summary>
public interface IKmsProvider
{
    /// <summary>提供方标识，出现在 <c>kms:&lt;provider&gt;/...</c> 前缀中，须唯一。</summary>
    string ProviderName { get; }

    /// <summary>
    /// 解密一个 KMS 密文的 Base64 载荷，返回明文密钥字节。
    /// 明文长度的含义由密文生产方约定（UTF8 文本 / base64 密钥字节等），
    /// 调用方（如规则解密注入）负责按目标格式还原为字符串。
    /// </summary>
    byte[] Decrypt(string ciphertextBase64);
}
