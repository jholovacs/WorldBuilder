using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Splat-style 8-bit masks derived from final terrain metrics (rock cliffs, sediment deposition, cumulative flow, sea-level water).
/// </summary>
public sealed class TerrainSplatmask
{
    internal TerrainSplatmask(int width, int height, byte[,] rockCliff, byte[,] sediment, byte[,] flow, byte[,] water, byte[,] lakeSink)
    {
        if (rockCliff.GetLength(0) != height || rockCliff.GetLength(1) != width)
            throw new ArgumentException("Mask dimensions mismatch.", nameof(rockCliff));
        Width = width;
        Height = height;
        RockCliffMask = rockCliff;
        SedimentMask = sediment;
        FlowMask = flow;
        WaterMask = water;
        LakeSinkMask = lakeSink;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Slope-driven rock/cliff mask (255 where terrain slope exceeds the cliff threshold).</summary>
    public byte[,] RockCliffMask { get; }

    /// <summary>Hydraulic deposition accumulated during erosion (normalized by tile max).</summary>
    public byte[,] SedimentMask { get; }

    /// <summary>Cumulative droplet flow exposure along particle paths (normalized by tile max).</summary>
    public byte[,] FlowMask { get; }

    /// <summary>255 where elevation &lt; sea level.</summary>
    public byte[,] WaterMask { get; }

    /// <summary>
    /// Depression-filled lakes after hydrological sink pass (normalized depth ×255); zeros when sink pass skipped.
    /// </summary>
    public byte[,] LakeSinkMask { get; }

    /// <summary>
    /// Builds masks from row-major buffers aligned with <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public static TerrainSplatmask Create(
        ReadOnlySpan<float> heightsMeters,
        ReadOnlySpan<float> hydraulicDeposit,
        ReadOnlySpan<float> cumulativeFlow,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        float cliffSlopeDegrees = 45f,
        byte[,]? lakeSinkMask = null)
    {
        int len = checked(width * height);
        if (heightsMeters.Length != len || hydraulicDeposit.Length != len || cumulativeFlow.Length != len)
            throw new ArgumentException("Expected spans of length width × height.");

        byte[,] lake =
            lakeSinkMask ?? new byte[height, width];

        float maxDep = 0f;
        float maxFlow = 0f;
        for (int i = 0; i < len; i++)
        {
            float d = hydraulicDeposit[i];
            float f = cumulativeFlow[i];
            if (d > maxDep)
                maxDep = d;
            if (f > maxFlow)
                maxFlow = f;
        }

        float invRangeDep = maxDep > 1e-12f ? 255f / maxDep : 0f;
        float invRangeFlow = maxFlow > 1e-12f ? 255f / maxFlow : 0f;

        float cliffRad = cliffSlopeDegrees * (MathF.PI / 180f);
        float tanCliff = MathF.Tan(cliffRad);
        float cell = Math.Max(1e-8f, cellSizeMeters);

        var rock = new byte[height, width];
        var sediment = new byte[height, width];
        var flow = new byte[height, width];
        var water = new byte[height, width];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float h = heightsMeters[i];
                water[y, x] = h < seaLevelMeters ? (byte)255 : (byte)0;
                sediment[y, x] = (byte)Math.Clamp(MathF.Round(hydraulicDeposit[i] * invRangeDep), 0f, 255f);
                flow[y, x] = (byte)Math.Clamp(MathF.Round(cumulativeFlow[i] * invRangeFlow), 0f, 255f);

                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    rock[y, x] = 0;
                    continue;
                }

                float gx = (heightsMeters[row + x + 1] - heightsMeters[row + x - 1]) / (2f * cell);
                float gy = (heightsMeters[row + x + width] - heightsMeters[row + x - width]) / (2f * cell);
                float slopeMag = MathF.Sqrt(gx * gx + gy * gy);
                rock[y, x] = slopeMag > tanCliff ? (byte)255 : (byte)0;
            }
        }

        return new TerrainSplatmask(width, height, rock, sediment, flow, water, lake);
    }

    /// <summary>Deep copy of mask layers (parallel export).</summary>
    public TerrainSplatmask CloneMasks()
    {
        int h = RockCliffMask.GetLength(0);
        int w = RockCliffMask.GetLength(1);

        byte[,] Dup(byte[,] plane)
        {
            var dst = new byte[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    dst[y, x] = plane[y, x];
            }

            return dst;
        }

        return new TerrainSplatmask(
            Width,
            Height,
            Dup(RockCliffMask),
            Dup(SedimentMask),
            Dup(FlowMask),
            Dup(WaterMask),
            Dup(LakeSinkMask));
    }

    /// <summary>
    /// Writes one grayscale PNG per mask (<c>rock_cliff.png</c>, <c>sediment.png</c>, <c>flow.png</c>, <c>water.png</c>, <c>lake_sink.png</c>).
    /// </summary>
    public void SaveMasksToPng(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        SavePlane(Path.Combine(directory, "rock_cliff.png"), RockCliffMask);
        SavePlane(Path.Combine(directory, "sediment.png"), SedimentMask);
        SavePlane(Path.Combine(directory, "flow.png"), FlowMask);
        SavePlane(Path.Combine(directory, "water.png"), WaterMask);
        SavePlane(Path.Combine(directory, "lake_sink.png"), LakeSinkMask);
    }

    private static void SavePlane(string path, byte[,] plane)
    {
        int h = plane.GetLength(0);
        int w = plane.GetLength(1);
        var pixels = new byte[w * h];
        int k = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                pixels[k++] = plane[y, x];
        }

        using var image = Image.LoadPixelData<L8>(pixels, w, h);
        image.Save(path, new PngEncoder());
    }
}
