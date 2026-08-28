using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ObjectAnalyzer.Security;

/// <summary>
/// 离线自托管 KMS 提供方：使用 AES-256-GCM 对称加密，密钥由用户口令经
/// SHA-256 派生，口令从环境变量 <c>OA_KMS_PASSPHRASE</c> 读取，
/// 不落在命令行参数、日志或 shell 历史中。
///
/// 密文载荷格式（Base64 包裹）：<c>[magic(4)][salt(16)][nonce(12)][tag(16)][ciphertext]</c>
/// - magic = <c>OA1G</c>（辨识 KMS 密文并校验口令正确性）
/// - salt = 每次加密随机生成的 16 字节盐（用于 KDF，确保同一口令不同密文）
/// - nonce = 12 字节 GCM nonce（每次随机）
/// - tag = 16 字节 GCM 认证标签（解密失败即口令错误/数据被篡改）
///
/// 规则文件引用形式：<c>kms:offline/1-&lt;encoding&gt;:&lt;payload&gt;</c>，
/// 其中 version 段 <c>1-&lt;encoding&gt;</c> 内的 encoding（utf8 / base64 / hex）声明明文的
/// 还原方式——加密时明文本就是字节，解密后据此还原为字符串（密钥常为 base64/hex 编码）。
/// </summary>
public sealed class OfflineKmsProvider : IKmsProvider
{
    public const string ProviderName = "offline";

    /// <summary>环境变量名（加密口令）。</summary>
    public const string PassphraseEnvVar = "OA_KMS_PASSPHRASE";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OA1G");

    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;

    string IKmsProvider.ProviderName => ProviderName;

    /// <summary>从环境变量读取口令；未设置或为空时抛错。</summary>
    public static string RequirePassphrase()
    {
        string passphrase = Environment.GetEnvironmentVariable(PassphraseEnvVar) ?? "";
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException(
                $"离线 KMS 需要设置环境变量 {PassphraseEnvVar}（加密口令），且口令不得为空。");
        }

        return passphrase;
    }

    public byte[] Decrypt(string ciphertextBase64)
    {
        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(ciphertextBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("KMS 密文不是合法的 Base64。", ex);
        }

        int expectedLen = Magic.Length + SaltLen + NonceLen + TagLen;
        if (envelope.Length < expectedLen + 1)
            throw new InvalidDataException("KMS 密文长度不足，可能不是由本工具生成。");

        // 校验 magic：口令错误或数据损坏时最先暴露
        for (int i = 0; i < Magic.Length; i++)
        {
            if (envelope[i] != Magic[i])
                throw new InvalidDataException("KMS 密文魔数不匹配（数据损坏或口令错误）。");
        }

        byte[] salt = envelope[Magic.Length..(Magic.Length + SaltLen)];
        byte[] nonce = envelope[(Magic.Length + SaltLen)..(Magic.Length + SaltLen + NonceLen)];
        byte[] tag = envelope[(Magic.Length + SaltLen + NonceLen)..(Magic.Length + SaltLen + NonceLen + TagLen)];
        byte[] ciphertext = envelope[(Magic.Length + SaltLen + NonceLen + TagLen)..];

        string passphrase = RequirePassphrase();
        byte[] key = DeriveKey(passphrase, salt);

        try
        {
            byte[] plain = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagLen);
            aes.Decrypt(nonce, ciphertext, tag, plain);
            return plain;
        }
        catch (CryptographicException ex)
        {
            // GCM 认证失败 = 口令错误或密文被篡改
            throw new InvalidDataException(
                $"离线 KMS 解密失败：口令错误或密文被篡改（{ex.Message}）。请检查 {PassphraseEnvVar} 是否与加密时一致。", ex);
        }
    }

    /// <summary>
    /// 用口令加密明文密钥并返回规则文件可直接使用的 KMS 引用字符串。
    /// <paramref name="plainText"/> 为明文（密钥常为 base64/hex 编码的文本），
    /// <paramref name="encoding"/> 声明其还原方式（utf8 / base64 / hex），写入 version 段供解密侧还原。
    /// </summary>
    public static string Encrypt(string plainText, string encoding, string? passphrase = null)
    {
        byte[] plainBytes = encoding.ToLowerInvariant() switch
        {
            "base64" => Convert.FromBase64String(plainText.Trim()),
            "hex" => Convert.FromHexString(plainText.Trim()),
            _ => Encoding.UTF8.GetBytes(plainText)
        };

        string pp = !string.IsNullOrEmpty(passphrase) ? passphrase! : RequirePassphrase();
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] key = DeriveKey(pp, salt);

        byte[] ciphertext = new byte[plainBytes.Length];
        byte[] tag = new byte[TagLen];
        using (var aes = new AesGcm(key, TagLen))
        {
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        }

        byte[] envelope = new byte[Magic.Length + SaltLen + NonceLen + TagLen + ciphertext.Length];
        Magic.CopyTo(envelope, 0);
        salt.CopyTo(envelope, Magic.Length);
        nonce.CopyTo(envelope, Magic.Length + SaltLen);
        tag.CopyTo(envelope, Magic.Length + SaltLen + NonceLen);
        ciphertext.CopyTo(envelope, Magic.Length + SaltLen + NonceLen + TagLen);

        string payload = Convert.ToBase64String(envelope);
        return $"kms:{ProviderName}/1-{encoding}:{payload}";
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        // SHA-256(password + salt) 一次迭代派生 256 位密钥。生产环境建议改用更高强度 KDF（如 PBKDF2/Argon2）
        // 以抵御离线口令爆破；本工具面向配置文件加密的便捷场景，SHA-256 已足够。
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(passphrase));
        sha.AppendData(salt);
        return sha.GetHashAndReset();
    }
}
