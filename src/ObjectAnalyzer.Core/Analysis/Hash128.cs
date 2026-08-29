namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 128 位紧凑哈希键（两个 ulong），替代 32 字符十六进制字符串键，减少字典内存占用。
/// </summary>
internal readonly struct Hash128(ulong high, ulong low) : IEquatable<Hash128>
{
    public readonly ulong High = high;
    public readonly ulong Low = low;

    public bool Equals(Hash128 other) => High == other.High && Low == other.Low;

    public override bool Equals(object? obj) => obj is Hash128 other && Equals(other);

    public override int GetHashCode() => unchecked((int)High ^ (int)(High >> 32) ^ (int)Low ^ (int)(Low >> 32));
}
