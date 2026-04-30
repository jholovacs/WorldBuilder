namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Optional Vulkan compute backend for noise + hydraulic erosion. Implementations live outside Domain (e.g. WorldBuilder.Gpu).
/// When <see cref="TryFillRidgedMultifractal"/> returns false, callers fall back to CPU paths.
/// </summary>
public interface ITerrainGpuCompute : IDisposable
{
    /// <summary>True when a Vulkan device with compute queue was initialized.</summary>
    bool IsAvailable { get; }

    /// <summary>Fills <paramref name="heights"/> with raw ridge stacking sums (unnormalized); callers must run <see cref="TerrainNoise.NormalizeRidgeBufferByMax"/> after scanning max.</summary>
    bool TryFillRidgedMultifractal(float[] heights, int width, int height, uint seed, in ErosionParams p);

    /// <summary>Hydraulic pass matching snapshot semantics; deposits splatted via GPU atomics then merged.</summary>
    /// <param name="afterPass">Optional — invoked after each hydraulic pass with <c>(passIndex1Based, totalPasses, heights)</c>; mirrors CPU <see cref="TerrainGenerationCallbacks.AfterHydraulicPass"/>.</param>
    bool TryHydraulicErosion(
        float[] heights,
        float[] depositAccum,
        float[] flowAccum,
        int width,
        int height,
        uint seed,
        in ErosionParams p,
        Action<int, int, float[]>? afterPass = null);

    /// <summary>
    /// Edge-preserving bilateral smoothing on normalized heights (post-erosion, pre meter scale).
    /// Return true when GPU applied the pass; callers should run the CPU fallback when false or unavailable.
    /// </summary>
    bool TryEdgePreservingBilateralSmooth(float[] heights, int width, int height, in ErosionParams p);

    /// <inheritdoc cref="ShallowWaterFlowMapOptions"/>
    /// <param name="biomeRgbaPng"><see langword="null"/> unless <paramref name="options"/>.IncludeBiomeRgbaPng is true and the GPU biome kernel succeeds. Channel <c>A</c>: GPU tree-density (white-noise × Worley clumping).</param>
    bool TryShallowWaterFlowMapPng(
        ReadOnlySpan<float> terrainNormalizedRowMajor,
        int width,
        int height,
        out byte[]? pngGray16,
        out byte[]? biomeRgbaPng,
        int iterations = 500,
        float initialWaterDepth = 0.02f,
        ShallowWaterFlowMapOptions? options = null);
}