namespace WorldBuilder.Domain.Terrain;

/// <summary>Immutable capture for engine export: POW2+1 height vertices and cloned splat at generation resolution.</summary>
public sealed class TerrainExportSnapshot
{
    public TerrainExportSnapshot(
        float[,] heightsMetersPow2Plus1,
        TerrainSplatmask splatmaskClonedOriginalResolution,
        ErosionParams erosionParamsUsed,
        float worldScaleMeters,
        byte[]? biomapRgbaPngSourceResolution,
        byte[]? vegetationScatterMaskPngSourceResolution)
    {
        HeightsMeters = heightsMetersPow2Plus1 ?? throw new ArgumentNullException(nameof(heightsMetersPow2Plus1));
        SplatmaskSourceResolution = splatmaskClonedOriginalResolution
            ?? throw new ArgumentNullException(nameof(splatmaskClonedOriginalResolution));
        ErosionParamsUsed = erosionParamsUsed;

        const float minWorld = 1f;
        if (worldScaleMeters < minWorld)
            throw new ArgumentOutOfRangeException(nameof(worldScaleMeters));

        WorldScaleMeters = worldScaleMeters;
        BiomapRgbaPngSourceResolution = biomapRgbaPngSourceResolution;
        VegetationScatterMaskPngSourceResolution = vegetationScatterMaskPngSourceResolution;
    }

    /// <summary>Vertex grid (typically 1025×1025): meters above implicit datum.</summary>
    public float[,] HeightsMeters { get; }

    /// <summary>Masks sampled on the procedural grid before POW2+1 upsample (cloned).</summary>
    public TerrainSplatmask SplatmaskSourceResolution { get; }

    public ErosionParams ErosionParamsUsed { get; }

    public float WorldScaleMeters { get; }

    /// <summary>RGBA biome mask at procedural resolution (same as splat source), when produced.</summary>
    public byte[]? BiomapRgbaPngSourceResolution { get; }

    /// <summary>Vegetation scatter L8 PNG at procedural resolution, when produced.</summary>
    public byte[]? VegetationScatterMaskPngSourceResolution { get; }

    public int VertexWidth => HeightsMeters.GetLength(1);

    public int VertexHeight => HeightsMeters.GetLength(0);

    /// <inheritdoc cref="TerrainExportService.CreateSnapshot"/>
    public static TerrainExportSnapshot Capture(
        TerrainGenerationResult result,
        ErosionParams resolved,
        float worldScaleMeters = 10_000f)
    {
        ArgumentNullException.ThrowIfNull(result);
        int sh = result.HeightsMeters.GetLength(0);
        int sw = result.HeightsMeters.GetLength(1);
        int vw = TerrainGridResampler.ToPow2PlusOneVertexCount(sw);
        int vh = TerrainGridResampler.ToPow2PlusOneVertexCount(sh);

        float[,] upsampled =
            vw == sw && vh == sh
                ? CopyHeights(result.HeightsMeters)
                : TerrainGridResampler.UpsampleBilinearFloat(result.HeightsMeters, vw, vh);

        return new TerrainExportSnapshot(
            upsampled,
            result.Splatmask.CloneMasks(),
            resolved,
            worldScaleMeters,
            result.BiomapRgbaPng,
            result.VegetationScatterMaskPng);
    }

    private static float[,] CopyHeights(float[,] src)
    {
        int h = src.GetLength(0);
        int w = src.GetLength(1);
        var dst = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y, x] = src[y, x];
        }

        return dst;
    }
}
