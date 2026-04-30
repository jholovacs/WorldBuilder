using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Builds a single-channel vegetation scatter PNG: white ≈ viable high tree-density cells (gates + stochastic multiplier).
/// </summary>
public static class TerrainVegetationScatterPass
{
    /// <remarks>
    /// <b>Systems under test:</b> export bundle scatter mask correctness from hydrology-derived moisture.<br/>
    /// <b>Cases:</b> flat wet lowland vs dry ridge vs steep cliff vs alpine above snow.<br/>
    /// <b>Expected:</b> only cells meeting moisture + slope + altitude gates survive binary threshold; HF noise avoids rectilinear blobs.<br/>
    /// <b>Why:</b> authoring tools need repeatable black/white foliage masks aligned with biome hydrology noise.
    /// </remarks>
    public static byte[] EncodeScatterMaskPng(
        ReadOnlySpan<float> heightsMeters,
        ReadOnlySpan<float> flowAccum,
        byte[,]? lakeSinkMaskOptional,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        uint scatterNoiseSeed,
        in VegetationScatterGenerationSettings scatter,
        in BiomapGenerationSettings blurSource,
        float snowLineMeters)
    {
        int len = checked(width * height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSizeMeters);
        if (heightsMeters.Length != len || flowAccum.Length != len)
            throw new ArgumentException("Span lengths must equal width × height.");
        if (!scatter.Enabled)
            throw new ArgumentException("Scatter encode requires Enabled.", nameof(scatter));

        float[,] blurredMoist =
            TerrainBiomapPass.ComputeBlurredWetOccupancy01(
                heightsMeters,
                flowAccum,
                lakeSinkMaskOptional,
                width,
                height,
                cellSizeMeters,
                seaLevelMeters,
                blurSource);

        float inv2c = 1f / (2f * cellSizeMeters);
        float threshM = scatter.MoistureThreshold01;
        float slopeMax = scatter.MaxSlopeDegrees;
        float nMin = Math.Clamp(scatter.NoiseMultiplierMin01, 0f, 1f);
        float nMax = Math.Clamp(scatter.NoiseMultiplierMax01, nMin + 1e-4f, 1f);
        float cutoff = Math.Clamp(scatter.BinaryCutoff01, 0.01f, 0.99f);
        float hz = Math.Clamp(scatter.ScatterNoiseScale, 4f, 512f);

        var pixelsRowMajor = new byte[len];
        int k = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float h = heightsMeters[i];
                float moist = blurredMoist[y, x];

                int xm = Math.Max(x - 1, 0);
                int xp = Math.Min(x + 1, width - 1);
                int ym = Math.Max(y - 1, 0);
                int yp = Math.Min(y + 1, height - 1);

                float gx = (heightsMeters[row + xp] - heightsMeters[row + xm]) * inv2c;
                float gy = (heightsMeters[yp * width + x] - heightsMeters[ym * width + x]) * inv2c;
                float slopeDeg = MathF.Atan(MathF.Sqrt(gx * gx + gy * gy)) * (180f / MathF.PI);

                bool gates =
                    moist > threshM &&
                    slopeDeg < slopeMax &&
                    h < snowLineMeters;

                float fine = PseudoCell01(x, y, scatterNoiseSeed ^ 0x4C8E_5513u);
                int macroStep = Math.Max(2, unchecked((int)Math.Round(Math.Clamp(hz / 12f + 3f, 3f, 96f))));
                float macro = PseudoCell01(x / macroStep, y / macroStep, scatterNoiseSeed ^ 0xA11E_FACEu);

                float n01 = Math.Clamp(fine * MathF.FusedMultiplyAdd(0.5f, macro, 0.5f), 0f, 1f);
                float mul = MathF.FusedMultiplyAdd(nMax - nMin, n01, nMin);
                float v = gates ? mul : 0f;
                pixelsRowMajor[k++] = v >= cutoff ? byte.MaxValue : (byte)0;
            }
        }

        using var img = Image.LoadPixelData<L8>(pixelsRowMajor, width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    internal static VegetationScatterGenerationSettings Resolved(VegetationScatterGenerationSettings incoming)
    {
        bool omit =
            !incoming.Enabled &&
            incoming.MoistureThreshold01 <= 1e-6f &&
            incoming.MaxSlopeDegrees <= 1e-6f;
        if (omit)
            return VegetationScatterGenerationSettings.Curated;

        if (!incoming.Enabled)
            return incoming;

        return Normalize(incoming);
    }

    private static VegetationScatterGenerationSettings Normalize(VegetationScatterGenerationSettings s)
    {
        float moistTh =
            s.MoistureThreshold01 <= 1e-6f
                ? VegetationScatterGenerationSettings.Curated.MoistureThreshold01
                : Math.Clamp(s.MoistureThreshold01, 0.08f, 0.92f);

        float slope =
            s.MaxSlopeDegrees <= 1e-3f
                ? VegetationScatterGenerationSettings.Curated.MaxSlopeDegrees
                : Math.Clamp(s.MaxSlopeDegrees, 5f, 55f);

        float hz =
            s.ScatterNoiseScale <= 1f
                ? VegetationScatterGenerationSettings.Curated.ScatterNoiseScale
                : Math.Clamp(s.ScatterNoiseScale, 4f, 512f);

        float nm = Math.Clamp(s.NoiseMultiplierMin01 <= 1e-6f ? 0.32f : s.NoiseMultiplierMin01, 0f, 0.98f);
        float nx = Math.Clamp(s.NoiseMultiplierMax01 <= 1e-6f ? 1f : s.NoiseMultiplierMax01, nm + 0.02f, 1f);
        float cut = Math.Clamp(s.BinaryCutoff01 <= 1e-6f ? 0.498f : s.BinaryCutoff01, 0.08f, 0.92f);

        return s with
        {
            Enabled = true,
            MoistureThreshold01 = moistTh,
            MaxSlopeDegrees = slope,
            ScatterNoiseScale = hz,
            NoiseMultiplierMin01 = nm,
            NoiseMultiplierMax01 = nx,
            BinaryCutoff01 = cut,
        };
    }

    /// <summary>Deterministic [0, ~1] per lattice cell — two octaves multiplied for speckling without crystalline grids.</summary>
    private static float PseudoCell01(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h ^= seed;
            h *= 2654435761u;
            h ^= h >> 16;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return (h >> 8) / 16777216f;
        }
    }
}
