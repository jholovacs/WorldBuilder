namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Final terrain heights in meters plus derived splat masks for engine import.
/// </summary>
public sealed class TerrainGenerationResult
{
    private readonly ReadOnlyMemory<float>? _flatHeightSamplesMeters;

    public TerrainGenerationResult(
        float[,] heightsMeters,
        TerrainSplatmask splatmask,
        ReadOnlyMemory<float>? flatHeightSamplesMeters = null,
        byte[]? biomapRgbaPng = null,
        byte[]? vegetationScatterMaskPng = null)
    {
        HeightsMeters = heightsMeters ?? throw new ArgumentNullException(nameof(heightsMeters));
        Splatmask = splatmask ?? throw new ArgumentNullException(nameof(splatmask));
        BiomapRgbaPng = biomapRgbaPng;
        VegetationScatterMaskPng = vegetationScatterMaskPng;

        int h = HeightsMeters.GetLength(0);
        int w = HeightsMeters.GetLength(1);
        if (flatHeightSamplesMeters is { Length: var len })
        {
            if (len != checked(w * h))
                throw new ArgumentException("Flat buffer length must match width × height.", nameof(flatHeightSamplesMeters));
            _flatHeightSamplesMeters = flatHeightSamplesMeters;
        }
        else
            _flatHeightSamplesMeters = null;
    }

    /// <summary>R snow / G veg / B rock / A dirt (8-bit PNG) at procedural grid resolution when <see cref="ErosionParams.Biomap"/> is enabled.</summary>
    public byte[]? BiomapRgbaPng { get; }

    /// <summary>L8 monochrome tree scatter mask when <see cref="ErosionParams.ScatterVegetation"/> is enabled.</summary>
    public byte[]? VegetationScatterMaskPng { get; }

    /// <summary>Heights in meters after min–max normalization × <see cref="ErosionParams.MaxElevationMeters"/>.</summary>
    public float[,] HeightsMeters { get; }

    public TerrainSplatmask Splatmask { get; }

    /// <summary>
    /// Row-major duplicate retained when <see cref="TerrainGenerator.Generate"/> uses export retention — avoids allocating another flatten pass for PNG encoding.
    /// </summary>
    public ReadOnlyMemory<float>? FlatHeightSamplesMeters => _flatHeightSamplesMeters;

    /// <summary>
    /// Writes row-major 16-bit unsigned integers (little-endian), scanline order with Y increasing:
    /// index <c>y * width + x</c>. Values linearly map tile min/max elevation to 0–65535 (document tile bounds externally).
    /// </summary>
    public void SaveToRaw(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        int height = HeightsMeters.GetLength(0);
        int width = HeightsMeters.GetLength(1);

        float minH = float.MaxValue;
        float maxH = float.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float v = HeightsMeters[y, x];
                if (v < minH)
                    minH = v;
                if (v > maxH)
                    maxH = v;
            }
        }

        float range = Math.Max(maxH - minH, 1e-8f);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float t = (HeightsMeters[y, x] - minH) / range;
                ushort q = (ushort)Math.Clamp(Math.Round(t * 65535f), 0d, 65535d);
                bw.Write(q);
            }
        }
    }

    /// <inheritdoc cref="TerrainSplatmask.SaveMasksToPng"/>
    public void SaveMasksToPng(string directory) => Splatmask.SaveMasksToPng(directory);
}
