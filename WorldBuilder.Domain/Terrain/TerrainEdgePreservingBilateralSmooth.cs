using System.Threading.Tasks;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Tomasi–Manduchi edge-preserving bilateral filter on normalized height samples.
/// Spatial Gaussian decays by pixel distance; range Gaussian preserves sharp ridges vs smooth plains/valleys.
/// </summary>
internal static class TerrainEdgePreservingBilateralSmooth
{
    internal static void Apply(
        float[] heights,
        int width,
        int height,
        float smoothingStrength,
        float smoothingRadiusPx)
    {
        if (smoothingStrength <= 0f || smoothingRadiusPx < 1f || width < 3 || height < 3)
            return;

        int count = checked(width * height);
        if (heights.Length != count)
            return;

        int r = Math.Clamp((int)MathF.Round(smoothingRadiusPx), 1, 16);
        float sigmaSpatial = Math.Max(smoothingRadiusPx * 0.45f + 1e-4f, 1e-3f);
        float sigmaRange = Math.Clamp(smoothingStrength * 0.12f, 1e-6f, 0.95f);

        float inv2SigmaSsq = 1f / (2f * sigmaSpatial * sigmaSpatial);
        float inv2SigmaRsq = 1f / (2f * sigmaRange * sigmaRange);

        var src = (float[])heights.Clone();
        Parallel.For(
            0,
            height,
            y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int centerIdx = row + x;
                    float hc = src[centerIdx];
                    float sumW = 0f;
                    float sumH = 0f;

                    for (int dy = -r; dy <= r; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= (uint)height)
                            continue;

                        int nrow = ny * width;
                        int dySq = dy * dy;

                        for (int dx = -r; dx <= r; dx++)
                        {
                            int nx = x + dx;
                            if ((uint)nx >= (uint)width)
                                continue;

                            float spatial = MathF.Exp(-((dx * dx + dySq) * inv2SigmaSsq));
                            float hn = src[nrow + nx];
                            float dh = hn - hc;
                            float rangeW = MathF.Exp(-(dh * dh * inv2SigmaRsq));
                            float w = spatial * rangeW;

                            sumW += w;
                            sumH += w * hn;
                        }
                    }

                    if (sumW <= 1e-12f)
                        heights[centerIdx] = hc;
                    else
                        heights[centerIdx] = Math.Clamp(sumH / sumW, 0f, 1f);
                }
            });
    }
}
