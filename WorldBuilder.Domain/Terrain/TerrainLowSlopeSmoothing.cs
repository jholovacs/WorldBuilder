using System.Threading.Tasks;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Interior cells with surface tilt below a fixed threshold replace height with the umbrella operator:
/// arithmetic mean of eight neighbors — one Jacobi Laplacian step that removes high-frequency jitter on gentle terrain
/// without moving steep ridges.
/// </summary>
internal static class TerrainLowSlopeSmoothing
{
    /// <remarks>
    /// Slope magnitude matches the hydraulic path: centered differences on normalized heights and
    /// <c>tan(θ) ≈ ||∇h|| · MaxElevationMeters / CellSizeMeters</c>, compared to tan(5°).
    /// </remarks>
    internal static void Apply(
        float[] heights,
        int width,
        int height,
        float cellSizeMeters,
        float maxElevationMeters)
    {
        if (width < 3 || height < 3 || cellSizeMeters <= 1e-6f || maxElevationMeters <= 1e-6f)
            return;

        int count = checked(width * height);
        if (heights.Length != count)
            return;

        float maxTanSlopeDeg5 = MathF.Tan(5f * MathF.PI / 180f);

        float invCell = maxElevationMeters / cellSizeMeters;
        var src = (float[])heights.Clone();
        var dst = (float[])src.Clone();

        Parallel.For(1, height - 1, y =>
        {
            int row = y * width;
            int rowMinus = row - width;
            int rowPlus = row + width;

            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                float gx = (src[i + 1] - src[i - 1]) * 0.5f;
                float gy = (src[rowPlus + x] - src[rowMinus + x]) * 0.5f;
                float slopeMag = MathF.Sqrt(gx * gx + gy * gy);
                float tanSlopeApprox = slopeMag * invCell;

                if (tanSlopeApprox < maxTanSlopeDeg5)
                {
                    float sum = src[rowMinus + x - 1]
                        + src[rowMinus + x]
                        + src[rowMinus + x + 1]
                        + src[i - 1]
                        + src[i + 1]
                        + src[rowPlus + x - 1]
                        + src[rowPlus + x]
                        + src[rowPlus + x + 1];
                    dst[i] = Math.Clamp(sum / 8f, 0f, 1f);
                }
            }
        });

        dst.AsSpan().CopyTo(heights);
    }
}
