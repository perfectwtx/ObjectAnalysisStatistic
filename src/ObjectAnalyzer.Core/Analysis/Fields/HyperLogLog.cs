namespace ObjectAnalyzer.Analysis;

/// <summary>
/// HyperLogLog 基数估计：默认精度 p=14（16384 寄存器，标准误差 ≈0.81%）。
/// 稀疏模式（字典）起步，寄存器命中超过阈值后升级为稠密字节数组；
/// 线程本地实例通过 <see cref="Merge"/>（逐寄存器取最大）合并到全局。
/// 估计策略：寄存器零值仍充足时优先 LinearCounting（消除原始估计在 n≈m 过渡区的系统性正偏差），
/// 否则退回标准 HLL 原始估计。
/// </summary>
public sealed class HyperLogLog(int precision = 14)
{
    private const int SparseThreshold = 256;
    private readonly int _p = ValidatePrecision(precision);
    private readonly int _m = 1 << precision;
    private Dictionary<int, byte>? _sparse;
    private byte[]? _dense;

    private static int ValidatePrecision(int precision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 20);
        return precision;
    }

    /// <summary>加入一个字符串值（内部做 FNV-1a 64 + murmur 终末混淆哈希）。</summary>
    public void Add(string value)
    {
        AddHash(HashString(value));
    }

    public void AddHash(ulong hash)
    {
        int register = (int)(hash >> (64 - _p));
        byte rank = (byte)(CountLeadingZeros(hash << _p, 64 - _p) + 1);

        if (_dense != null)
        {
            if (_dense[register] < rank)
                _dense[register] = rank;
            return;
        }

        _sparse ??= new Dictionary<int, byte>();
        if (_sparse.TryGetValue(register, out byte existing))
        {
            if (existing < rank)
                _sparse[register] = rank;
        }
        else
        {
            _sparse[register] = rank;
            if (_sparse.Count >= SparseThreshold)
                PromoteToDense();
        }
    }

    /// <summary>逐寄存器取最大合并（合并后保持当前实例的存储形态）。</summary>
    public void Merge(HyperLogLog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_p != other._p) throw new ArgumentException("HyperLogLog precisions differ.", nameof(other));
        // 空源无贡献
        if (other._dense == null && (other._sparse == null || other._sparse.Count == 0))
            return;

        // 自身尚未初始化：直接复制对方状态
        if (_dense == null && _sparse == null)
        {
            if (other._dense != null)
            {
                EnsureDense();
                Array.Copy(other._dense, _dense!, _m);
            }
            else
            {
                _sparse = new Dictionary<int, byte>(other._sparse!);
            }
            return;
        }

        if (other._dense != null)
        {
            EnsureDense();
            for (int i = 0; i < _m; i++)
            {
                if (_dense![i] < other._dense[i])
                    _dense[i] = other._dense[i];
            }
        }
        else
        {
            foreach (var kv in other._sparse!)
            {
                if (_dense != null)
                {
                    if (_dense[kv.Key] < kv.Value)
                        _dense[kv.Key] = kv.Value;
                }
                else
                {
                    _sparse!.TryGetValue(kv.Key, out byte existing);
                    if (existing < kv.Value)
                        _sparse[kv.Key] = kv.Value;
                }
            }
            if (_sparse != null && _sparse.Count >= SparseThreshold)
                PromoteToDense();
        }
    }

    /// <summary>基数估计：优先 LinearCounting（零寄存器充足时更准），否则标准 HLL 原始估计。</summary>
    public long Estimate()
    {
        if (_dense != null)
        {
            double sum = 0;
            int zeros = 0;
            for (int i = 0; i < _m; i++)
            {
                byte r = _dense[i];
                sum += 1.0 / (1UL << r);
                if (r == 0)
                    zeros++;
            }

            double alphaM = Alpha(_m);
            double rawEstimate = alphaM * _m * _m / sum;

            // 零寄存器充足时 LC 无偏且方差更低；同时保留经典 E≤2.5m 切换条件
            if (zeros > 0 && (rawEstimate <= 2.5 * _m || zeros > _m * 0.01))
            {
                return (long)Math.Round(_m * Math.Log((double)_m / zeros)); // LinearCounting
            }

            return (long)Math.Round(rawEstimate);
        }

        if (_sparse == null || _sparse.Count == 0)
            return 0;

        // 稀疏态：触达寄存器数即不同桶数，LinearCounting 估计
        int zerosSparse = _m - _sparse.Count;
        return (long)Math.Round(_m * Math.Log((double)_m / zerosSparse));
    }

    private void PromoteToDense()
    {
        EnsureDense();
        foreach (var kv in _sparse!)
        {
            if (_dense![kv.Key] < kv.Value)
                _dense[kv.Key] = kv.Value;
        }
        _sparse = null;
    }

    private void EnsureDense() => _dense ??= new byte[_m];

    internal HyperLogLogStateSnapshot Capture()
    {
        var data = new HyperLogLogStateSnapshot { Precision = _p };
        if (_dense is not null)
        {
            for (int i = 0; i < _m; i++) if (_dense[i] > 0) data.Registers.Add(i, _dense[i]);
        }
        else if (_sparse is not null) data.Registers = new(_sparse);
        return data;
    }

    internal static HyperLogLog Restore(HyperLogLogStateSnapshot data)
    {
        var hll = new HyperLogLog(data.Precision);
        foreach (var pair in data.Registers)
        {
            if (pair.Key < 0 || pair.Key >= hll._m || pair.Value == 0 || pair.Value > 65 - data.Precision)
                throw new InvalidDataException("Invalid HyperLogLog register.");
        }
        hll._sparse = new(data.Registers);
        if (hll._sparse.Count >= SparseThreshold) hll.PromoteToDense();
        return hll;
    }

    private static byte CountLeadingZeros(ulong bits, int maxBits)
    {
        if (bits == 0)
            return (byte)maxBits;
        int lz = System.Numerics.BitOperations.LeadingZeroCount(bits);
        return (byte)Math.Min(lz, maxBits);
    }

    private static double Alpha(int m) => m switch
    {
        16 => 0.673,
        32 => 0.697,
        64 => 0.709,
        _ => 0.7213 / (1.0 + 1.079 / m)
    };

    /// <summary>FNV-1a 64 位遍历字符 + murmur3 终末混淆（avalanche），适配 HLL 位分布需求。</summary>
    internal static ulong HashString(string value)
    {
        ulong h = 14695981039346656037UL;
        const ulong prime = 0x100000001b3UL;
        foreach (char c in value)
        {
            h ^= c;
            h *= prime;
            h ^= (ulong)(c >> 8);
            h *= prime;
        }
        // murmur3 finalizer
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }
}
