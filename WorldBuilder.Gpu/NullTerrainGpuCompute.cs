using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

/// <summary>Placeholder when Vulkan compute is unavailable — CPU erosion/noise paths run unchanged.</summary>
public sealed class NullTerrainGpuCompute : ITerrainGpuCompute
{
    public static NullTerrainGpuCompute Instance { get; } = new();

    private NullTerrainGpuCompute()
    {
    }

    public bool IsAvailable => false;

    public bool TryFillRidgedMultifractal(float[] heights, int width, int height, uint seed, in ErosionParams p) =>
        false;

    public bool TryHydraulicErosion(
        float[] heights,
        float[] depositAccum,
        float[] flowAccum,
        int width,
        int height,
        uint seed,
        in ErosionParams p,
        Action<int, int, float[]>? afterPass = null) =>
        false;

    public bool TryEdgePreservingBilateralSmooth(float[] heights, int width, int height, in ErosionParams p) =>
        false;

    public bool TryShallowWaterFlowMapPng(
        ReadOnlySpan<float> terrainNormalizedRowMajor,
        int width,
        int height,
        out byte[]? pngGray16,
        out byte[]? biomeRgbaPng,
        int iterations = 500,
        float initialWaterDepth = 0.02f,
        ShallowWaterFlowMapOptions? options = null)
    {
        pngGray16 = null;
        biomeRgbaPng = null;
        return false;
    }

    public void Dispose()
    {
    }
}
