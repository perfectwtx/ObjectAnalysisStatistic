using System.Text;
using ObjectAnalyzer.Security;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// KMS 集成测试：离线提供方（AES-GCM + 环境变量口令）的加解密往返、
/// kms: 前缀解析、规则加载时对 transform 敏感字段的密文→明文注入，以及 AES 端到端解密闭环。
/// </summary>
public class KmsTests
{
    private const string TestPassphrase = "kms-test-passphrase-1234";
    private const string EnvVar = "OA_KMS_PASSPHRASE";

    // ── 加解密往返（AES-256-GCM）──

    [Fact]
    public void OfflineKms_EncryptDecrypt_RoundTrips_Utf8()
    {
        string kmsRef = OfflineKmsProvider.Encrypt("super-secret-key", "utf8", TestPassphrase);
        Assert.StartsWith("kms:offline/1-utf8:", kmsRef, StringComparison.Ordinal);

        SetPassphrase();
        try
        {
            Assert.Equal("super-secret-key", KmsSecretResolver.Resolve(kmsRef));
        }
        finally
        {
            ClearPassphrase();
        }
    }

    [Fact]
    public void OfflineKms_Base64Key_RoundTripsEncoded()
    {
        // 明文为 base64 编码的密钥字节；解密侧按 base64 重新编码还原，与原始值一致
        string base64Key = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef"));
        string kmsRef = OfflineKmsProvider.Encrypt(base64Key, "base64", TestPassphrase);
        Assert.StartsWith("kms:offline/1-base64:", kmsRef, StringComparison.Ordinal);

        SetPassphrase();
        try
        {
            Assert.Equal(base64Key, KmsSecretResolver.Resolve(kmsRef));
        }
        finally
        {
            ClearPassphrase();
        }
    }

    [Fact]
    public void OfflineKms_HexKey_RoundTripsEncoded()
    {
        string hexKey = Convert.ToHexString(Encoding.UTF8.GetBytes("0123456789abcdef"));
        string kmsRef = OfflineKmsProvider.Encrypt(hexKey, "hex", TestPassphrase);
        Assert.StartsWith("kms:offline/1-hex:", kmsRef, StringComparison.Ordinal);

        SetPassphrase();
        try
        {
            Assert.Equal(hexKey, KmsSecretResolver.Resolve(kmsRef));
        }
        finally
        {
            ClearPassphrase();
        }
    }

    [Fact]
    public void OfflineKms_SamePlain_SamePassphrase_DifferentCiphertext()
    {
        // 每次加密随机 salt/nonce，两次密文不同（语义安全）
        var a = OfflineKmsProvider.Encrypt("k", "utf8", TestPassphrase);
        var b = OfflineKmsProvider.Encrypt("k", "utf8", TestPassphrase);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OfflineKms_WrongPassphrase_Throws()
    {
        string kmsRef = OfflineKmsProvider.Encrypt("secret", "utf8", TestPassphrase);
        Environment.SetEnvironmentVariable(EnvVar, "wrong-passphrase");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => KmsSecretResolver.Resolve(kmsRef));
            Assert.Contains("口令错误或密文被篡改", ex.Message);
        }
        finally
        {
            ClearPassphrase();
        }
    }

    [Fact]
    public void OfflineKms_MissingPassphrase_Throws()
    {
        ClearPassphrase();
        string kmsRef = OfflineKmsProvider.Encrypt("secret", "utf8", TestPassphrase);
        var ex = Assert.Throws<InvalidOperationException>(() => KmsSecretResolver.Resolve(kmsRef));
        Assert.Contains(EnvVar, ex.Message);
    }

    // ── 前缀解析 ──

    [Fact]
    public void Resolver_NonKmsValue_PassthroughUnchanged()
    {
        Assert.Equal("SGVsbG8=", KmsSecretResolver.Resolve("SGVsbG8="));
        Assert.Equal("", KmsSecretResolver.Resolve(null));
        Assert.False(KmsSecretResolver.IsKmsReference("SGVsbG8="));
        Assert.True(KmsSecretResolver.IsKmsReference("kms:offline/1-utf8:xxx"));
    }

    [Fact]
    public void Resolver_UnknownProvider_Throws()
    {
        SetPassphrase();
        try
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => KmsSecretResolver.Resolve("kms:nonexistent/1-utf8:QUJD"));
            Assert.Contains("未知的 KMS 提供方", ex.Message);
        }
        finally
        {
            ClearPassphrase();
        }
    }

    // ── 规则加载注入 ──

    [Fact]
    public void RuleResolver_DecryptsTransformSensitiveFields()
    {
        string keyRef = OfflineKmsProvider.Encrypt(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")), "base64", TestPassphrase);
        string ivRef = OfflineKmsProvider.Encrypt(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("abcdefghijklmnop")), "base64", TestPassphrase);

        var rules = new AnalysisRules();
        rules.Fields["secret"] = new FieldRule
        {
            Transform = new FieldTransform { Type = "aes", Key = keyRef, Iv = ivRef }
        };
        // 非敏感/普通字段不触碰
        rules.Fields["plain"] = new FieldRule { Transform = new FieldTransform { Type = "base64" } };

        SetPassphrase();
        try
        {
            KmsRuleResolver.Resolve(rules);
        }
        finally
        {
            ClearPassphrase();
        }

        var tr = rules.Fields["secret"].Transform!;
        Assert.False(tr.Key!.StartsWith("kms:", StringComparison.Ordinal)); // 已被替换为明文
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")), tr.Key);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("abcdefghijklmnop")), tr.Iv);
        Assert.Equal("base64", rules.Fields["plain"].Transform!.Type); // 无 kms 引用，不受影响
    }

    [Fact]
    public void RuleResolver_AesEndToEnd_DecryptsValue()
    {
        // 完整闭环：真实 AES 加密数据 → 规则里 key/iv 用 KMS 密文 → KmsRuleResolver 解密 → FieldTransformProcessor 正常解码
        byte[] key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        byte[] iv = Encoding.UTF8.GetBytes("abcdefghijklmnop");
        string plainText = "kms-integrated-data";

        byte[] cipher;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plainText), 0, plainText.Length);
        }

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "secret", Convert.ToBase64String(cipher) } }
        };

        // 规则文件里 key/iv 只放 KMS 密文引用，明文不落盘
        var rules = new AnalysisRules();
        rules.Fields["secret"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "aes",
                Key = OfflineKmsProvider.Encrypt(Convert.ToBase64String(key), "base64", TestPassphrase),
                Iv = OfflineKmsProvider.Encrypt(Convert.ToBase64String(iv), "base64", TestPassphrase)
            }
        };

        SetPassphrase();
        try
        {
            KmsRuleResolver.Resolve(rules);
        }
        finally
        {
            ClearPassphrase();
        }

        var processor = new FieldTransformProcessor(rules);
        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.First(f => f.FieldName == "secret");
        Assert.Equal(plainText, stat.ValueCounts.Keys.Single());
        Assert.Equal(0, stat.TransformFailureCount);
    }

    // ── 帮助方法 ──

    private static void SetPassphrase() =>
        Environment.SetEnvironmentVariable(EnvVar, TestPassphrase);

    private static void ClearPassphrase() =>
        Environment.SetEnvironmentVariable(EnvVar, null);
}
