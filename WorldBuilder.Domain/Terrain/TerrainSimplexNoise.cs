using System.Runtime.CompilerServices;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// 2D Simplex noise (gradient simplex kernel), deterministic per <paramref name="seed"/>.
/// Output roughly in [-1, 1].
/// </summary>
internal static class TerrainSimplexNoise
{
    private static readonly float F2 = (MathF.Sqrt(3f) - 1f) / 2f;
    private static readonly float G2 = (3f - MathF.Sqrt(3f)) / 6f;

    /// <summary>Sample Simplex noise at <paramref name="x"/>, <paramref name="y"/> (continuous domain).</summary>
    internal static float Sample2D(float x, float y, uint seed)
    {
        float s = (x + y) * F2;
        float xi = x + s;
        float yi = y + s;
        int i = FastFloor(xi);
        int j = FastFloor(yi);
        float t = (i + j) * G2;
        float X0 = i - t;
        float Y0 = j - t;
        float x0 = x - X0;
        float y0 = y - Y0;

        int i1 = x0 > y0 ? 1 : 0;
        int j1 = x0 > y0 ? 0 : 1;

        float x1 = x0 - i1 + G2;
        float y1 = y0 - j1 + G2;
        float x2 = x0 - 1f + 2f * G2;
        float y2 = y0 - 1f + 2f * G2;

        ulong salt = TerrainNoise.SplitMix64(seed ^ 0x53D259FEUL);

        float n0 = Contribution(i, j, x0, y0, salt ^ 0x9E3779B185EBCA87UL);
        float n1 = Contribution(i + i1, j + j1, x1, y1, salt ^ 0xC2B2AE3D27741FCDUL);
        float n2 = Contribution(i + 1, j + 1, x2, y2, salt ^ 0x165667B19E3779F9UL);

        return 70f * (n0 + n1 + n2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastFloor(float v) => (int)Math.Floor(v);

    private static float Contribution(int gi, int gj, float dx, float dy, ulong cornerSalt)
    {
        float t = 0.5f - dx * dx - dy * dy;
        if (t <= 0f)
            return 0f;

        t *= t;
        ulong cell = (ulong)unchecked((long)gi * 73856093L ^ (long)gj * 19349663L);
        ulong mixed = TerrainNoise.SplitMix64(cornerSalt ^ cell);
        float angle = (mixed >> 40) / (float)(1 << 24) * (MathF.PI * 2f);
        float gx = MathF.Cos(angle);
        float gy = MathF.Sin(angle);
        float grad = gx * dx + gy * dy;
        return t * t * grad;
    }
}
