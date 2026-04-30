using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Builds a unified RGBA biome / splat PNG after erosion + hydrology: R snow, G vegetation, B rock/cliff, A dirt/sand.
/// </summary>
public static class TerrainBiomapPass
{
    /// <summary>Produces 8-bit-per-channel RGBA PNG (same resolution as terrain grid).</summary>
    public static byte[] EncodeBiomapRgbaPng(
        ReadOnlySpan<float> heightsMeters,
        ReadOnlySpan<float> depositAccum,
        ReadOnlySpan<float> flowAccum,
        byte[,]? lakeSinkMaskOptional,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        uint noiseSeed,
        in BiomapGenerationSettings biome)
    {
        int len = checked(width * height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSizeMeters);
        if (heightsMeters.Length != len || depositAccum.Length != len || flowAccum.Length != len)
            throw new ArgumentException("Span lengths must equal width × height.");

        float cell = cellSizeMeters;
        float inv2c = 1f / (2f * cell);

        float maxFlow = 0f;
        float maxDep = 0f;
        for (int i = 0; i < len; i++)
        {
            float f = flowAccum[i];
            float d = depositAccum[i];
            if (f > maxFlow)
                maxFlow = f;
            if (d > maxDep)
                maxDep = d;
        }

        float invMaxDep = maxDep > 1e-18f ? 1f / maxDep : 0f;

        float[,] blurred = ComputeBlurredWetOccupancy01(
            heightsMeters,
            flowAccum,
            lakeSinkMaskOptional,
            width,
            height,
            cellSizeMeters,
            seaLevelMeters,
            biome);

        float invMaxFlow = maxFlow > 1e-18f ? 1f / maxFlow : 0f;

        var pixels = new Rgba32[len];
        int k = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float h = heightsMeters[i];

                float n = PseudoNoiseSignedMeters(x, y, noiseSeed) * biome.SnowJitterMeters;
                float edgeLo = biome.SnowLineMeters - biome.SnowSoftBandMeters + n;
                float edgeHi = biome.SnowLineMeters + biome.SnowSoftBandMeters + n;
                byte snowByte = QuantizeSmoothStep(h, edgeLo, edgeHi);

                float flowN = flowAccum[i] * invMaxFlow;
                float depN = depositAccum[i] * invMaxDep;
                float moist = blurred[y, x];
                float vegRaw =
                    biome.VegetationMoistureWeight * moist +
                    biome.VegetationFlowWeight * Saturate01(flowN) +
                    biome.VegetationDepositWeight * Saturate01(depN);
                vegRaw = Saturate01(vegRaw);

                int xm = Math.Max(x - 1, 0);
                int xp = Math.Min(x + 1, width - 1);
                int ym = Math.Max(y - 1, 0);
                int yp = Math.Min(y + 1, height - 1);

                float gx = (heightsMeters[row + xp] - heightsMeters[row + xm]) * inv2c;
                float gy = (heightsMeters[yp * width + x] - heightsMeters[ym * width + x]) * inv2c;
                float slopeMag = MathF.Sqrt(gx * gx + gy * gy);
                float slopeDeg = MathF.Atan(slopeMag) * (180f / MathF.PI);

                float cliffDeg = biome.CliffSlopeDegrees;
                float cliffSoft = biome.CliffSlopeSoftDegrees;
                float rockF01 = slopeDeg <= cliffDeg - cliffSoft
                    ? 0f
                    : slopeDeg >= cliffDeg + cliffSoft
                        ? 1f
                        : (slopeDeg - (cliffDeg - cliffSoft)) / (2f * cliffSoft);

                byte rockByte = QuantizeSoftThreshold(
                    slopeDeg,
                    cliffDeg - cliffSoft * 0.5f,
                    cliffDeg + cliffSoft * 1.5f,
                    inverted: false);

                float vegMasked = Saturate01(vegRaw * (1f - rockF01));
                byte vegByte = (byte)Math.Clamp((int)Math.Round(vegMasked * 255f), 0, 255);

                float snowF01 = snowByte / 255f;
                float rem = Saturate01(1f - MathF.Max(MathF.Max(snowF01, vegMasked), rockF01));
                float sedBoost = Saturate01((depN - biome.DirtSedimentIntensity01) /
                    Math.Max(1e-6f, 1f - biome.DirtSedimentIntensity01));

                float alphaF =
                    biome.DirtSedimentVsComplementWeight * Saturate01(sedBoost + depN * 0.85f)
                    + (1f - biome.DirtSedimentVsComplementWeight) * rem;

                alphaF = Saturate01(alphaF);
                byte dirtByte = (byte)Math.Clamp((int)Math.Round(alphaF * 255f), 0, 255);

                pixels[k++] = new Rgba32(snowByte, vegByte, rockByte, dirtByte);
            }
        }

        using var img = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <summary>Gaussian-blurred wet occupancy compatible with biome moisture — ocean, lake sink weights, flux corridors.</summary>
    internal static float[,] ComputeBlurredWetOccupancy01(
        ReadOnlySpan<float> heightsMeters,
        ReadOnlySpan<float> flowAccum,
        byte[,]? lakeSinkMaskOptional,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        in BiomapGenerationSettings biome)
    {
        int len = checked(width * height);
        if (heightsMeters.Length != len || flowAccum.Length != len)
            throw new ArgumentException("Span lengths must equal width × height.");

        float cell = Math.Max(cellSizeMeters, 1e-6f);
        float maxFlow = 0f;
        for (int i = 0; i < len; i++)
        {
            float f = flowAccum[i];
            if (f > maxFlow)
                maxFlow = f;
        }

        float invMaxFlow = maxFlow > 1e-18f ? 1f / maxFlow : 0f;

        float reachMin = Math.Max(biome.MoistureReachMinMeters, cell);
        float reachMax = Math.Max(biome.MoistureReachMaxMeters, reachMin + cell);
        float blurSigma = Math.Clamp(biome.MoistureBlurSigmaMeters, reachMin * 0.5f, reachMax * 2f);

        int blurRadiusPx = (int)Math.Clamp(Math.Round(blurSigma / cell), 2.0, 96.0);
        int iterations = Math.Max(2, blurRadiusPx / 2);

        var wetOccupancy = new float[height, width];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float h = heightsMeters[i];
                float ocean = Saturate01(h < seaLevelMeters ? 1f : 0f);
                float lakeWeight = lakeSinkMaskOptional is byte[,] lake && lake[y, x] != 0
                    ? lake[y, x] / 255f
                    : 0f;

                float flowN = flowAccum[i] * invMaxFlow;
                float corridor = Saturate01((flowN - 0.03f) * 5f);

                float baseWet = MathF.Max(MathF.Max(ocean, lakeWeight), corridor * 0.42f * flowN);

                wetOccupancy[y, x] = Saturate01(baseWet);
            }
        }

        return ApproximateGaussianBlur(wetOccupancy, width, height, blurRadiusPx, iterations);
    }

    private static byte QuantizeSmoothStep(float v, float e0, float e1)
    {
        float t = e1 <= e0 + 1e-6f ? (v >= e0 ? 1f : 0f) : SmoothStep(e0, e1, v);
        t = Saturate01(t);
        return (byte)Math.Clamp((int)Math.Round(t * 255f), 0, 255);
    }

    private static byte QuantizeSoftThreshold(float v, float e0, float e1, bool inverted)
    {
        float t = SmoothStep(e0, e1, v);
        if (inverted)
            t = 1f - t;
        return (byte)Math.Clamp((int)Math.Round(Saturate01(t) * 255f), 0, 255);
    }

    /// <summary>Separable approximate Gaussian via repeated symmetric box blur.</summary>
    private static float[,] ApproximateGaussianBlur(float[,] src, int width, int height, int radius, int repeats)
    {
        int maxSpan = Math.Max(width, height);
        radius = Math.Max(1, Math.Min(radius, Math.Max(maxSpan - 1, 1)));

        var a = new float[height, width];
        var b = new float[height, width];
        Copy2D(src, a, width, height);

        for (int repeat = 0; repeat < repeats; repeat++)
        {
            HorizontalBox(a, b, width, height, radius);
            VerticalBox(b, a, width, height, radius);
        }

        return a;
    }

    private static void Copy2D(float[,] src, float[,] dst, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y, x] = src[y, x];
        }
    }

    private static void HorizontalBox(float[,] src, float[,] dst, int width, int height, int r)
    {
        r = Math.Clamp(r, 1, width - 1);
        int denom = Math.Min(width, r * 2 + 1);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = Math.Clamp(x + dx, 0, width - 1);
                    sum += src[y, xx];
                }

                dst[y, x] = Saturate01(sum / denom);
            }
        }
    }

    private static void VerticalBox(float[,] src, float[,] dst, int width, int height, int r)
    {
        r = Math.Clamp(r, 1, height - 1);
        int denom = Math.Min(height, r * 2 + 1);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float sum = 0f;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, height - 1);
                    sum += src[yy, x];
                }

                dst[y, x] = Saturate01(sum / denom);
            }
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (x <= edge0)
            return 0f;
        if (x >= edge1)
            return 1f;
        float t = (x - edge0) / (edge1 - edge0);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Deterministic jitter in ±1 range scaled by callers to meters.</summary>
    private static float PseudoNoiseSignedMeters(int x, int y, uint seed)
    {
        uint h = unchecked((uint)(x * 374761393) + (uint)(y * 668265263) + seed + 374761391u);
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;

        float t = (h & 0xFFFFFu) / 1048576f;

        return t * 2f - 1f;
    }

    private static float Saturate01(float f) =>
        f < 0f ? 0f : (f > 1f ? 1f : f);

    public static BiomapGenerationSettings Resolved(BiomapGenerationSettings incoming)
    {
        if (ShouldTreatAsCuratedDefaults(incoming))
            return NormalizeClamped(BiomapGenerationSettings.Curated);

        if (!incoming.Enabled)
            return incoming;

        return NormalizeClamped(incoming);
    }

    private static bool ShouldTreatAsCuratedDefaults(BiomapGenerationSettings b) =>
        !b.Enabled &&
        b.SnowLineMeters <= 0f &&
        b.MoistureReachMinMeters <= 1e-6f &&
        b.MoistureReachMaxMeters <= 1e-6f;

    private static BiomapGenerationSettings NormalizeClamped(BiomapGenerationSettings b)
    {
        float snowLine =
            b.SnowLineMeters <= 10f ? BiomapGenerationSettings.Curated.SnowLineMeters : b.SnowLineMeters;

        float rMin = b.MoistureReachMinMeters <= 1e-6f
            ? BiomapGenerationSettings.Curated.MoistureReachMinMeters
            : b.MoistureReachMinMeters;

        float rMax = b.MoistureReachMaxMeters <= rMin
            ? BiomapGenerationSettings.Curated.MoistureReachMaxMeters
            : b.MoistureReachMaxMeters;

        rMax = Math.Max(rMax, rMin + 1f);

        float blur =
            b.MoistureBlurSigmaMeters <= 1e-6f
                ? BiomapGenerationSettings.Curated.MoistureBlurSigmaMeters
                : b.MoistureBlurSigmaMeters;
        blur = Math.Clamp(blur, rMin * 0.5f, rMax * 2f);

        float cliffDeg = Math.Clamp(
            b.CliffSlopeDegrees <= 1f ? BiomapGenerationSettings.Curated.CliffSlopeDegrees : b.CliffSlopeDegrees,
            5f,
            80f);

        float cliffSoft = Math.Clamp(
            b.CliffSlopeSoftDegrees <= 1e-3f ? 4f : b.CliffSlopeSoftDegrees,
            0.25f,
            15f);

        return b with
        {
            Enabled = true,
            SnowLineMeters = snowLine,
            SnowSoftBandMeters = Math.Max(
                20f,
                b.SnowSoftBandMeters <= 1f ? BiomapGenerationSettings.Curated.SnowSoftBandMeters : b.SnowSoftBandMeters),
            SnowJitterMeters =
                Math.Clamp(
                    b.SnowJitterMeters < 1f ? BiomapGenerationSettings.Curated.SnowJitterMeters : b.SnowJitterMeters,
                    4f,
                    300f),

            MoistureReachMinMeters = rMin,
            MoistureReachMaxMeters = rMax,
            MoistureBlurSigmaMeters = blur,

            VegetationFlowWeight =
                Math.Clamp(b.VegetationFlowWeight <= 1e-6f ? 0.55f : b.VegetationFlowWeight, 0f, 3f),

            VegetationMoistureWeight =
                Math.Clamp(b.VegetationMoistureWeight <= 1e-6f ? 0.82f : b.VegetationMoistureWeight, 0f, 3f),

            VegetationDepositWeight =
                Math.Clamp(b.VegetationDepositWeight <= 1e-6f ? 0.28f : b.VegetationDepositWeight, 0f, 2f),

            CliffSlopeDegrees = cliffDeg,

            CliffSlopeSoftDegrees = cliffSoft,

            DirtSedimentIntensity01 =
                Math.Clamp(b.DirtSedimentIntensity01 <= 1e-6f ? 0.38f : b.DirtSedimentIntensity01, 0.05f, 0.95f),

            DirtSedimentVsComplementWeight = Math.Clamp(
                Math.Abs(b.DirtSedimentVsComplementWeight) < 1e-6f ? 0.65f : b.DirtSedimentVsComplementWeight,
                0f,
                1f),
        };
    }
}
