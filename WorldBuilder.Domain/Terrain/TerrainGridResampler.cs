namespace WorldBuilder.Domain.Terrain;

/// <summary>Resamples sampled height/mask grids toward Unity-style (<c>2^n + 1</c>) vertex counts.</summary>
public static class TerrainGridResampler
{
    /// <summary>
    /// Converts a sample count (e.g. 1024²) to Unity-style <c>vertexCount = 2^k + 1</c> heightmap width/height.
    /// If <paramref name="samples"/> is already <c>pow2 + 1</c>, returns it unchanged.
    /// If <paramref name="samples"/> is pure <c>2^k</c>, returns <c>samples + 1</c>.
    /// Otherwise returns <c>CeilingPow2(samples) + 1</c>.
    /// </summary>
    public static int ToPow2PlusOneVertexCount(int samples)
    {
        if (samples < 2)
            throw new ArgumentOutOfRangeException(nameof(samples));

        int n = samples;
        ulong nm1 = (ulong)n - 1UL;
        if (nm1 > 0 && (nm1 & (nm1 - 1)) == 0)
            return n;

        ulong un = (ulong)n;
        if (un > 0 && (un & (un - 1)) == 0)
            return n + 1;

        return CeilingToPowerOfTwo(n) + 1;
    }

    private static int CeilingToPowerOfTwo(int v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }

    public static float[,] UpsampleBilinearFloat(float[,] src, int dstW, int dstH)
    {
        int sh = src.GetLength(0);
        int sw = src.GetLength(1);
        var dst = new float[dstH, dstW];

        for (int y = 0; y < dstH; y++)
        {
            float ty = dstH <= 1 ? 0f : y / (dstH - 1f);
            float fy = ty * (sh - 1);
            for (int x = 0; x < dstW; x++)
            {
                float tx = dstW <= 1 ? 0f : x / (dstW - 1f);
                float fx = tx * (sw - 1);
                dst[y, x] = SampleBilinear(src, sw, sh, fx, fy);
            }
        }

        return dst;
    }

    public static byte[,] UpsampleBilinearByte(byte[,] src, int dstW, int dstH)
    {
        int sh = src.GetLength(0);
        int sw = src.GetLength(1);
        var dst = new byte[dstH, dstW];

        for (int y = 0; y < dstH; y++)
        {
            float ty = dstH <= 1 ? 0f : y / (dstH - 1f);
            float fy = ty * (sh - 1);
            for (int x = 0; x < dstW; x++)
            {
                float tx = dstW <= 1 ? 0f : x / (dstW - 1f);
                float fx = tx * (sw - 1);
                float v = SampleBilinearByte(src, sw, sh, fx, fy);
                dst[y, x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        }

        return dst;
    }

    private static float SampleBilinear(float[,] src, int sw, int sh, float fx, float fy)
    {
        float x0 = Math.Clamp(MathF.Floor(fx), 0, sw - 1);
        float y0 = Math.Clamp(MathF.Floor(fy), 0, sh - 1);
        float x1 = Math.Clamp(x0 + 1f, 0, sw - 1);
        float y1 = Math.Clamp(y0 + 1f, 0, sh - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        int ix0 = (int)x0;
        int iy0 = (int)y0;
        int ix1 = (int)x1;
        int iy1 = (int)y1;

        float c00 = src[iy0, ix0];
        float c10 = src[iy0, ix1];
        float c01 = src[iy1, ix0];
        float c11 = src[iy1, ix1];
        float xa = c00 + (c10 - c00) * tx;
        float xb = c01 + (c11 - c01) * tx;
        return xa + (xb - xa) * ty;
    }

    private static float SampleBilinearByte(byte[,] src, int sw, int sh, float fx, float fy)
    {
        float x0 = Math.Clamp(MathF.Floor(fx), 0, sw - 1);
        float y0 = Math.Clamp(MathF.Floor(fy), 0, sh - 1);
        float x1 = Math.Clamp(x0 + 1f, 0, sw - 1);
        float y1 = Math.Clamp(y0 + 1f, 0, sh - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        int ix0 = (int)x0;
        int iy0 = (int)y0;
        int ix1 = (int)x1;
        int iy1 = (int)y1;

        float c00 = src[iy0, ix0];
        float c10 = src[iy0, ix1];
        float c01 = src[iy1, ix0];
        float c11 = src[iy1, ix1];
        float xa = c00 + (c10 - c00) * tx;
        float xb = c01 + (c11 - c01) * tx;
        return xa + (xb - xa) * ty;
    }
}
