using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Utilities.Encoders;

namespace ObjectAnalyzer.Analysis;

public sealed partial class FieldTransformProcessor
{
    // ── SM4 解密（国密对称算法，128 位块 / 128 位密钥，GM/T 0002-2014）──
    private static string ApplySm4(FieldTransform transform, string cipherText)
    {
        if (transform.Key == null)
            throw new InvalidDataException("SM4 转换缺少 key 配置。");

        byte[] key = DecodeBytes(transform.Key, transform.Format);
        if (key.Length != 16)
            throw new InvalidDataException($"SM4 密钥长度必须为 16 字节（128 位），当前 {key.Length} 字节。");

        byte[] cipher;
        try
        {
            cipher = DecodeBytes(cipherText, transform.Format);
        }
        catch (FormatException)
        {
            // format 指定的解码方式失败时按 base64 重试（密文本身就是 base64 字符串）
            cipher = Convert.FromBase64String(cipherText);
        }

        IBlockCipher engine = new SM4Engine();
        IBlockCipher mode;
        bool isEcb = string.Equals(transform.Mode, "ecb", StringComparison.OrdinalIgnoreCase);
        if (isEcb)
        {
            mode = engine;
        }
        else
        {
            if (transform.Iv == null)
                throw new InvalidDataException("SM4 CBC 模式需要提供 iv。");
            byte[] iv = DecodeBytes(transform.Iv, transform.Format);
            if (iv.Length != 16)
                throw new InvalidDataException($"SM4 IV 长度必须为 16 字节，当前 {iv.Length} 字节。");
            mode = new CbcBlockCipher(engine);
        }

        PaddedBufferedBlockCipher cipher2;
        if (string.Equals(transform.Padding, "none", StringComparison.OrdinalIgnoreCase))
        {
            cipher2 = new PaddedBufferedBlockCipher(mode, new ZeroBytePadding());
            // ZeroBytePadding 在解密时不会去掉末尾 0；改用无填充处理后手动 trim
        }
        else
        {
            cipher2 = new PaddedBufferedBlockCipher(mode, new Pkcs7Padding());
        }

        var keyParam = new KeyParameter(key);
        ICipherParameters cipherParams = keyParam;
        if (!isEcb)
        {
            byte[] ivBytes = DecodeBytes(transform.Iv!, transform.Format);
            cipherParams = new ParametersWithIV(keyParam, ivBytes);
        }

        cipher2.Init(false, cipherParams);
        byte[] output = new byte[cipher2.GetOutputSize(cipher.Length)];
        int len = cipher2.ProcessBytes(cipher, 0, cipher.Length, output, 0);
        len += cipher2.DoFinal(output, len);

        // PKCS7 填充已被 Pkcs7Padding 剥离；ZeroBytePadding 需要手动去掉尾部 0
        if (string.Equals(transform.Padding, "none", StringComparison.OrdinalIgnoreCase))
        {
            int newLen = len;
            while (newLen > 0 && output[newLen - 1] == 0) newLen--;
            len = newLen;
        }

        return Encoding.UTF8.GetString(output, 0, len);
    }

    // ── SM2 转换（国密非对称算法，GM/T 0003-2014）：decrypt 私钥解公钥密文 / verify 公钥验签 ──
    private static string ApplySm2(FieldTransform transform, string cipherText)
    {
        string sm2Mode = string.IsNullOrWhiteSpace(transform.Mode) ? "decrypt" : transform.Mode.Trim().ToLowerInvariant();
        if (sm2Mode == "decrypt")
            return ApplySm2Decrypt(transform, cipherText);
        if (sm2Mode == "verify")
            return ApplySm2Verify(transform, cipherText);
        throw new InvalidDataException($"SM2 mode 不支持: {transform.Mode}（支持 decrypt / verify）。");
    }

    private static string ApplySm2Decrypt(FieldTransform transform, string cipherText)
    {
        if (transform.PrivateKey == null)
            throw new InvalidDataException("SM2 decrypt 缺少 privateKey 配置。");

        ECPrivateKeyParameters privKey = LoadSm2PrivateKey(transform.PrivateKey, transform.Format);
        byte[] cipher;
        try
        {
            cipher = DecodeBytes(cipherText, transform.Format);
        }
        catch (FormatException)
        {
            cipher = Convert.FromBase64String(cipherText);
        }

        var engine = new SM2Engine();
        engine.Init(false, privKey);
        byte[] plain = engine.ProcessBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static string ApplySm2Verify(FieldTransform transform, string plainText)
    {
        if (transform.PublicKey == null || transform.Signature == null)
            throw new InvalidDataException("SM2 verify 缺少 publicKey / signature 配置。");

        ECPublicKeyParameters pubKey = LoadSm2PublicKey(transform.PublicKey, transform.Format);
        byte[] sig = DecodeBytes(transform.Signature, transform.Format);
        byte[] data = Encoding.UTF8.GetBytes(plainText);

        var signer = new SM2Signer();
        signer.Init(false, pubKey);
        signer.BlockUpdate(data, 0, data.Length);
        bool valid = signer.VerifySignature(sig);
        return valid ? "valid" : "invalid";
    }

    private static ECPrivateKeyParameters LoadSm2PrivateKey(string keyMaterial, string? format)
    {
        // 优先尝试：1) hex 32 字节私钥整数；2) PEM/DER PKCS#8
        string trimmed = keyMaterial.Trim();
        bool isHex = string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase);

        if (isHex || LooksLikeHex(trimmed))
        {
            byte[] bytes = isHex ? Hex.Decode(trimmed) : Hex.Decode(trimmed);
            if (bytes.Length != 32)
                throw new InvalidDataException($"SM2 私钥 hex 长度必须为 32 字节，当前 {bytes.Length}。");
            var d = new BigInteger(1, bytes);
            return new ECPrivateKeyParameters(d, GetSm2DomainParameters());
        }

        // Base64：先尝试 DER (PKCS#8 PrivateKeyInfo)，失败则回退到裸 32 字节
        try
        {
            var (priv, _) = ReadPemPair(trimmed, expectPrivate: true);
            return priv ?? throw new InvalidDataException("PEM 中未找到 SM2 私钥。");
        }
        catch
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length != 32)
                throw new InvalidDataException($"SM2 私钥（base64 裸）长度必须为 32 字节，当前 {bytes.Length}。");
            var d = new BigInteger(1, bytes);
            return new ECPrivateKeyParameters(d, GetSm2DomainParameters());
        }
    }

    private static ECPublicKeyParameters LoadSm2PublicKey(string keyMaterial, string? format)
    {
        string trimmed = keyMaterial.Trim();
        bool isHex = string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase);

        if (isHex || LooksLikeHex(trimmed))
        {
            byte[] bytes = isHex ? Hex.Decode(trimmed) : Hex.Decode(trimmed);
            // 04 || X (32) || Y (32)
            if (bytes.Length == 65 && bytes[0] == 0x04)
            {
                var x = new BigInteger(1, bytes.AsSpan(1, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(33, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            if (bytes.Length == 64)
            {
                var x = new BigInteger(1, bytes.AsSpan(0, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(32, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            throw new InvalidDataException($"SM2 公钥 hex 长度必须为 64 字节（裸 X||Y）或 65 字节（04||X||Y），当前 {bytes.Length}。");
        }

        // Base64：先尝试 DER (X.509 SubjectPublicKeyInfo)，失败则回退到裸 04||X||Y
        try
        {
            var (_, pub) = ReadPemPair(trimmed, expectPrivate: false);
            return pub ?? throw new InvalidDataException("PEM 中未找到 SM2 公钥。");
        }
        catch
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length == 65 && bytes[0] == 0x04)
            {
                var x = new BigInteger(1, bytes.AsSpan(1, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(33, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            throw new InvalidDataException($"SM2 公钥（base64）需为 65 字节未压缩点 (04||X||Y)，当前 {bytes.Length}。");
        }
    }

    private static ECDomainParameters GetSm2DomainParameters()
    {
        // sm2p256v1 曲线参数（GM/T 0003-2014 附录 A）
        X9ECParameters x9 = ECNamedCurveTable.GetByName("sm2p256v1")
            ?? throw new InvalidOperationException("BouncyCastle 未注册 sm2p256v1 曲线。");
        return new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H, x9.GetSeed());
    }

    private static bool LooksLikeHex(string s)
    {
        if (s.Length == 0) return false;
        if (s.Length % 2 != 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static (ECPrivateKeyParameters? priv, ECPublicKeyParameters? pub) ReadPemPair(string material, bool expectPrivate)
    {
        var reader = new PemReader(new System.IO.StringReader(material));
        object obj = reader.ReadObject();
        if (obj == null) throw new InvalidDataException("PEM 输入为空。");

        if (obj is AsymmetricCipherKeyPair pair)
        {
            return (pair.Private as ECPrivateKeyParameters, pair.Public as ECPublicKeyParameters);
        }
        if (obj is ECPrivateKeyParameters ecPriv)
        {
            return (ecPriv, null);
        }
        if (obj is ECPublicKeyParameters ecPub)
        {
            return (null, ecPub);
        }
        if (obj is AsymmetricKeyParameter akp)
        {
            // 私钥 PrivateKeyInfo 走 AsymmetricCipherKeyPair 分支，这里兜底 PKCS#8 私钥
            if (akp is ECPrivateKeyParameters p) return (p, null);
            if (akp is ECPublicKeyParameters pb) return (null, pb);
        }
        throw new InvalidDataException($"PEM 类型不被识别: {obj.GetType().Name}（expectPrivate={expectPrivate}）。");
    }
}
