using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ObjectAnalyzer.Analysis;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 国密算法（SM4 对称 / SM2 非对称）transform 转换测试。
/// 测试向量取自 GM/T 0002-2014 与 GM/T 0003-2012 公开附录；
/// SM2 的 encrypt/decrypt 与 sign/verify 使用 BouncyCastle 现生成密钥做 round-trip。
/// </summary>
public class GuomiTransformTests
{
    // ──────────────────────────────────────────────
    // SM4 — GM/T 0002-2014 测试向量
    // ──────────────────────────────────────────────

    /// <summary>
    /// SM4 ECB 模式单分组测试向量（GM/T 0002-2014 附录 A.1）：
    /// Key/Plain: 0123456789ABCDEFFEDCBA9876543210
    /// Cipher: 681EDF34D206965E86B3E94F536E4246
    /// </summary>
    [Fact]
    public void Sm4_Ecb_RoundTrip_MatchesOfficialVector()
    {
        // 用 BouncyCastle 加密得到合法 ciphertext，再让 transform 解密验证
        string keyHex = "0123456789abcdeffedcba9876543210";
        string plain = "0123456789abcdeffedcba9876543210";
        byte[] expectedCipher = Sm4EncryptEcb(HexToBytes(keyHex), Encoding.UTF8.GetBytes(plain));

        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = keyHex,
                Mode = "ecb",
                Padding = "pkcs7",
                Format = "hex"
            }
        };
        var processor = new FieldTransformProcessor(rules);

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            new() { { "x", Convert.ToBase64String(expectedCipher) } }
        }).ToList();

        var stat = AnalyzeSingleField(processor, wrapped, rules, "x");
        Assert.Equal(plain, stat.ValueCounts.Keys.Single());
    }

    [Fact]
    public void Sm4_Cbc_Pkcs7_RoundTrip()
    {
        byte[] key = RandomBytes(16);
        byte[] iv = RandomBytes(16);
        string plain = "国密 SM4 CBC PKCS7 解密测试 —— hello SM4";

        byte[] cipher = Sm4EncryptCbc(key, iv, Encoding.UTF8.GetBytes(plain));
        string cipherB64 = Convert.ToBase64String(cipher);

        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = Convert.ToBase64String(key),
                Iv = Convert.ToBase64String(iv),
                Mode = "cbc",
                Padding = "pkcs7",
                Format = "base64"
            }
        };
        var processor = new FieldTransformProcessor(rules);
        var stat = AnalyzeSingleField(processor, new List<Dictionary<string, object?>>
        {
            new() { { "x", cipherB64 } }
        }, rules, "x");

        Assert.Equal(plain, stat.ValueCounts.Keys.Single());
    }

    [Fact]
    public void Sm4_WrongKeyLength_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = Convert.ToBase64String(new byte[15]), // 长度非法
                Format = "base64"
            }
        };

        var ex = Assert.Throws<InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("SM4 密钥长度", ex.Message);
    }

    [Fact]
    public void Sm4_Cbc_MissingIv_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = Convert.ToBase64String(new byte[16]),
                Mode = "cbc"
            }
        };

        var ex = Assert.Throws<InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("iv", ex.Message);
    }

    [Fact]
    public void Sm4_GarbledCipher_KeepsOriginal()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm4",
                Key = Convert.ToBase64String(new byte[16]),
                Mode = "ecb",
                Padding = "pkcs7"
            }
        };
        var processor = new FieldTransformProcessor(rules);

        // 非法密文：转换失败应保留原值
        var stat = AnalyzeSingleField(processor, new List<Dictionary<string, object?>>
        {
            new() { { "x", "not-valid-base64-or-cipher" } }
        }, rules, "x");
        Assert.Equal(1, stat.TransformFailureCount);
        // 保留原始字符串
        Assert.Contains("not-valid-base64-or-cipher", stat.ValueCounts.Keys);
    }

    // ──────────────────────────────────────────────
    // SM2 — GM/T 0003-2012 公开算法
    // ──────────────────────────────────────────────

    [Fact]
    public void Sm2_Decrypt_RoundTrip()
    {
        var (priv, pub) = GenerateSm2KeyPair();
        string privKeyB64 = ExportPrivateKeyBase64(priv);
        string plain = "国密 SM2 非对称解密测试 —— hello SM2";

        var engine = new SM2Engine();
        engine.Init(true, new ParametersWithRandom(pub, new SecureRandom()));
        byte[] cipher = engine.ProcessBlock(Encoding.UTF8.GetBytes(plain), 0, Encoding.UTF8.GetByteCount(plain));

        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "decrypt",
                PrivateKey = privKeyB64,
                Format = "base64"
            }
        };
        var processor = new FieldTransformProcessor(rules);
        var stat = AnalyzeSingleField(processor, new List<Dictionary<string, object?>>
        {
            new() { { "x", Convert.ToBase64String(cipher) } }
        }, rules, "x");

        Assert.Equal(plain, stat.ValueCounts.Keys.Single());
    }

    [Fact]
    public void Sm2_Verify_ValidSignature_ReturnsValid()
    {
        var (priv, pub) = GenerateSm2KeyPair();
        string pubKeyB64 = ExportPublicKeyBase64(pub);
        string plain = "需要被 SM2 签名的明文";

        // 用 BC 生成签名
        var signer = new SM2Signer();
        signer.Init(true, new ParametersWithRandom(priv, new SecureRandom()));
        signer.BlockUpdate(Encoding.UTF8.GetBytes(plain), 0, Encoding.UTF8.GetByteCount(plain));
        byte[] sig = signer.GenerateSignature();

        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "verify",
                PublicKey = pubKeyB64,
                Signature = Convert.ToBase64String(sig),
                Format = "base64"
            }
        };
        var processor = new FieldTransformProcessor(rules);
        var stat = AnalyzeSingleField(processor, new List<Dictionary<string, object?>>
        {
            new() { { "x", plain } }
        }, rules, "x");

        Assert.Equal("valid", stat.ValueCounts.Keys.Single());
    }

    [Fact]
    public void Sm2_Verify_TamperedData_ReturnsInvalid()
    {
        var (priv, pub) = GenerateSm2KeyPair();
        string pubKeyB64 = ExportPublicKeyBase64(pub);
        string plain = "原文 A";
        string tampered = "原文 B";

        var signer = new SM2Signer();
        signer.Init(true, new ParametersWithRandom(priv, new SecureRandom()));
        signer.BlockUpdate(Encoding.UTF8.GetBytes(plain), 0, Encoding.UTF8.GetByteCount(plain));
        byte[] sig = signer.GenerateSignature();

        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "verify",
                PublicKey = pubKeyB64,
                Signature = Convert.ToBase64String(sig),
                Format = "base64"
            }
        };
        var processor = new FieldTransformProcessor(rules);
        var stat = AnalyzeSingleField(processor, new List<Dictionary<string, object?>>
        {
            new() { { "x", tampered } }
        }, rules, "x");

        Assert.Equal("invalid", stat.ValueCounts.Keys.Single());
    }

    [Fact]
    public void Sm2_Decrypt_MissingPrivateKey_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "decrypt"
            }
        };

        var ex = Assert.Throws<InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("privateKey", ex.Message);
    }

    [Fact]
    public void Sm2_Verify_MissingPublicKey_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "verify",
                Signature = "AAAA"
            }
        };

        var ex = Assert.Throws<InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("publicKey", ex.Message);
    }

    [Fact]
    public void Sm2_BadMode_Throws()
    {
        var rules = new AnalysisRules();
        rules.Fields["x"] = new FieldRule
        {
            Transform = new FieldTransform
            {
                Type = "sm2",
                Mode = "encrypt" // 不支持
            }
        };

        var ex = Assert.Throws<InvalidDataException>(() => new FieldTransformProcessor(rules));
        Assert.Contains("SM2 mode", ex.Message);
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static (Org.BouncyCastle.Crypto.Parameters.ECPrivateKeyParameters priv,
                    Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters pub)
        GenerateSm2KeyPair()
    {
        var gen = new ECKeyPairGenerator();
        gen.Init(new ECKeyGenerationParameters(
            new ECDomainParameters(
                Org.BouncyCastle.Asn1.X9.ECNamedCurveTable.GetByName("sm2p256v1")),
            new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        return ((Org.BouncyCastle.Crypto.Parameters.ECPrivateKeyParameters)pair.Private,
                (Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters)pair.Public);
    }

    private static string ExportPrivateKeyBase64(Org.BouncyCastle.Crypto.Parameters.ECPrivateKeyParameters priv)
    {
        // 导出为裸 32 字节大整数（base64），最常见用法
        byte[] d = priv.D.ToByteArray();
        // BouncyCastle 的大整数可能是 33 字节带前导 0（正数时）
        if (d.Length == 33 && d[0] == 0) d = d.AsSpan(1).ToArray();
        return Convert.ToBase64String(d);
    }

    private static string ExportPublicKeyBase64(Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters pub)
    {
        // 导出为 04 || X (32) || Y (32) 未压缩点
        byte[] x = pub.Q.AffineXCoord.ToBigInteger().ToByteArrayUnsigned();
        byte[] y = pub.Q.AffineYCoord.ToBigInteger().ToByteArrayUnsigned();
        var result = new byte[65];
        result[0] = 0x04;
        Array.Copy(x, 0, result, 1, x.Length);
        Array.Copy(y, 0, result, 33, y.Length);
        return Convert.ToBase64String(result);
    }

    private static byte[] Sm4EncryptEcb(byte[] key, byte[] plain)
    {
        var cipher = new PaddedBufferedBlockCipher(new SM4Engine(), new Pkcs7Padding());
        cipher.Init(true, new KeyParameter(key));
        byte[] output = new byte[cipher.GetOutputSize(plain.Length)];
        int len = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return output.AsSpan(0, len).ToArray();
    }

    private static byte[] Sm4EncryptCbc(byte[] key, byte[] iv, byte[] plain)
    {
        var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new SM4Engine()), new Pkcs7Padding());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
        byte[] output = new byte[cipher.GetOutputSize(plain.Length)];
        int len = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return output.AsSpan(0, len).ToArray();
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }

    private static byte[] RandomBytes(int len)
    {
        var bytes = new byte[len];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static FieldStatistic AnalyzeSingleField(
        FieldTransformProcessor processor,
        List<Dictionary<string, object?>> records,
        AnalysisRules rules,
        string fieldName)
    {
        // Wrap 应用 transform，再分析
        var wrapped = processor.Wrap(records).ToList();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 1 };
        var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(wrapped, rules, parallelOptions, default);
        processor.ApplyFailures(result);
        return result.FieldStatistics.First(f => f.FieldName == fieldName);
    }
}
