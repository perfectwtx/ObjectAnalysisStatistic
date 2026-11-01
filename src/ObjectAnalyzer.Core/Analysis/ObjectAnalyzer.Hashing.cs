using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

public static partial class ObjectAnalyzer
{
    /// <summary>
    /// 128 位非密码学哈希：两条不同种子的 FNV-1a 64 通道（第二条按字节逆序）加 MurmurHash3 雪崩混合。
    /// 仅用于整行去重，128 位空间足够抵抗碰撞，且远快于 MD5。
    /// </summary>
    private static Hash128 ComputeHash128(ReadOnlySpan<byte> data)
    {
        ulong h1 = 14695981039346656037UL;
        ulong h2 = 0x84222325cbf29ce4UL;
        const ulong prime = 0x100000001b3UL;

        foreach (byte b in data)
        {
            h1 ^= b;
            h1 *= prime;
        }
        for (int i = data.Length - 1; i >= 0; i--)
        {
            h2 ^= data[i];
            h2 *= prime;
        }

        // 雪崩混合，降低低位相关性
        h1 ^= h1 >> 33;
        h1 *= 0xff51afd7ed558ccdUL;
        h1 ^= h1 >> 33;
        h2 ^= h2 >> 33;
        h2 *= 0xc4ceb9fe1a85ec53UL;
        h2 ^= h2 >> 33;

        return new Hash128(h1, h2);
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> span = writer.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        writer.Advance(written);
    }

    private static void WriteAsciiNumber(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(16);
        Utf8Formatter.TryFormat(value, span, out int written);
        writer.Advance(written);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static string SerializeDuplicate(KeyValuePair<string, object?>[] sortedEntries)
    {
        try
        {
            var sortedDict = new Dictionary<string, object?>(sortedEntries.Length, StringComparer.Ordinal);
            foreach (var kv in sortedEntries)
            {
                sortedDict[kv.Key] = kv.Value;
            }
            return RecordValue.FromObject(sortedDict).ToJsonString(indented: true);
        }
        catch
        {
            var fallback = new Dictionary<string, string>(sortedEntries.Length);
            foreach (var kv in sortedEntries)
            {
                fallback[kv.Key] = SafeToString(kv.Value);
            }
            return JsonSerializer.Serialize(fallback, JsonSharedOptions.Write);
        }
    }

    private static string SafeToString(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        try { return value.ToString() ?? ""; }
        catch { return "[unserializable]"; }
    }
}
