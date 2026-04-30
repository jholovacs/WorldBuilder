using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WorldBuilder.Api.Terrain;

/// <summary>Deterministic SHA256-derived Gaussians keyed by world identity + seed + counter.</summary>
public static class CryptoTerrainSampler
{
    public static void FillStandardNormals(ReadOnlySpan<byte> worldIdBigEndian, ulong worldSeed, Span<float> dst)
    {
        Span<byte> seedMaterial = stackalloc byte[worldIdBigEndian.Length + 8];
        worldIdBigEndian.CopyTo(seedMaterial);
        BinaryPrimitives.WriteUInt64LittleEndian(seedMaterial[worldIdBigEndian.Length..], worldSeed);

        Span<byte> hash32 = stackalloc byte[32];
        Span<byte> block = stackalloc byte[seedMaterial.Length + 8];

        ulong counter = 0;
        var idx = 0;
        while (idx < dst.Length)
        {
            seedMaterial.CopyTo(block);
            BinaryPrimitives.WriteUInt64LittleEndian(block[seedMaterial.Length..], counter++);
            SHA256.HashData(block, hash32);

            double u1 = UniformOpen01(hash32[..8]);
            double u2 = UniformOpen01(hash32[8..16]);
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            double theta = 2.0 * Math.PI * u2;
            dst[idx++] = (float)(r * Math.Cos(theta));
            if (idx >= dst.Length)
                break;

            double u3 = UniformOpen01(hash32[16..24]);
            double u4 = UniformOpen01(hash32[24..]);
            double r2 = Math.Sqrt(-2.0 * Math.Log(u3));
            double theta2 = 2.0 * Math.PI * u4;
            dst[idx++] = (float)(r2 * Math.Cos(theta2));
        }
    }

    private static double UniformOpen01(ReadOnlySpan<byte> eightBytes)
    {
        ulong x = BitConverter.ToUInt64(eightBytes);
        const ulong mask = (1UL << 53) - 1UL;
        double mantissa = (x & mask) / (double)(1UL << 53);
        return Math.Clamp(mantissa, double.Epsilon, 1.0 - double.Epsilon);
    }

    /// <summary>Deterministic [0, 2⁶⁴) integer keyed by world id + seed + advancing counter.</summary>
    public static ulong NextUInt64(ReadOnlySpan<byte> worldIdBigEndian, ulong worldSeed, ref ulong counter)
    {
        Span<byte> seedMaterial = stackalloc byte[worldIdBigEndian.Length + 8];
        worldIdBigEndian.CopyTo(seedMaterial);
        BinaryPrimitives.WriteUInt64LittleEndian(seedMaterial[worldIdBigEndian.Length..], worldSeed);

        Span<byte> hash32 = stackalloc byte[32];
        Span<byte> block = stackalloc byte[seedMaterial.Length + 8];
        seedMaterial.CopyTo(block);
        BinaryPrimitives.WriteUInt64LittleEndian(block[seedMaterial.Length..], counter++);
        SHA256.HashData(block, hash32);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash32[..8]);
    }

    /// <summary>Inclusive range [minInclusive, maxInclusive].</summary>
    public static int UniformIntInclusive(ReadOnlySpan<byte> worldIdBigEndian, ulong worldSeed, ref ulong counter, int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxInclusive));

        uint span = (uint)(maxInclusive - minInclusive + 1);
        ulong r = NextUInt64(worldIdBigEndian, worldSeed, ref counter);
        return minInclusive + (int)(r % span);
    }

    /// <summary>Inclusive range [min, max].</summary>
    public static float UniformFloatInclusive(ReadOnlySpan<byte> worldIdBigEndian, ulong worldSeed, ref ulong counter, float min, float max)
    {
        if (max < min)
            throw new ArgumentOutOfRangeException(nameof(max));

        ulong r = NextUInt64(worldIdBigEndian, worldSeed, ref counter);
        double u = r / (double)ulong.MaxValue;
        return min + (float)(u * (max - min));
    }
}
