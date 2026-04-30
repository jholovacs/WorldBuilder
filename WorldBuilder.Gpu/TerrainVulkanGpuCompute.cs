using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

/// <summary>
/// Vulkan compute backend — GPU ridged multifractal + hydraulic droplet erosion (SPIR-V).
/// </summary>
public sealed class TerrainVulkanGpuCompute : ITerrainGpuCompute
{
    private float[]? _gpuHydrologyNormalized;
    private int _hydrologyW;
    private int _hydrologyH;
    private bool _hydrologyFromGpuHydrated;

    private readonly VulkanGpuContext _ctx;
    private readonly TerrainComputeShaderWrapper _noise;
    private readonly HydraulicComputeShaderWrapper _hydraulic;
    private readonly ShallowWaterComputeShaderWrapper? _shallowWater;
    private readonly BiomeComputeShaderWrapper? _calculateBiomes;

    private TerrainVulkanGpuCompute(
        VulkanGpuContext ctx,
        TerrainComputeShaderWrapper noise,
        HydraulicComputeShaderWrapper hydraulic,
        ShallowWaterComputeShaderWrapper? shallowWater,
        BiomeComputeShaderWrapper? calculateBiomes)
    {
        _ctx = ctx;
        _noise = noise;
        _hydraulic = hydraulic;
        _shallowWater = shallowWater;
        _calculateBiomes = calculateBiomes;
    }

    public bool IsAvailable => true;

    /// <summary>Attempts Vulkan initialization + shader load. Returns null when unavailable.</summary>
    public static TerrainVulkanGpuCompute? TryCreate()
    {
        try
        {
            var ctx = new VulkanGpuContext();
            var noiseSpirv = VulkanGpuContext.LoadEmbeddedSpirv("RidgedNoise.spv");
            var noise = new TerrainComputeShaderWrapper(ctx, noiseSpirv);
            var hydraulicSpirv = VulkanGpuContext.LoadEmbeddedSpirv("Hydraulic.spv");
            var hydraulic = new HydraulicComputeShaderWrapper(ctx, hydraulicSpirv);
            ShallowWaterComputeShaderWrapper? shallowWater = null;
            try
            {
                var flux = VulkanGpuContext.LoadEmbeddedSpirv("ShallowWaterFlux.spv");
                var upd = VulkanGpuContext.LoadEmbeddedSpirv("ShallowWaterUpdate.spv");
                shallowWater = new ShallowWaterComputeShaderWrapper(ctx, flux, upd);
            }
            catch
            {
                /* optional — erosion/noise GPU still usable */
            }

            BiomeComputeShaderWrapper? biomes = null;
            try
            {
                var bioSpirv = VulkanGpuContext.LoadEmbeddedSpirv("CalculateBiomes.spv");
                biomes = new BiomeComputeShaderWrapper(ctx, bioSpirv);
            }
            catch
            {
                /* optional when SPIR-V missing */
            }

            return new TerrainVulkanGpuCompute(ctx, noise, hydraulic, shallowWater, biomes);
        }
        catch
        {
            return null;
        }
    }

    public bool TryFillRidgedMultifractal(float[] heights, int width, int height, uint seed, in ErosionParams p)
    {
        try
        {
            var lac = p.Lacunarity > 1e-6f ? p.Lacunarity : 2f;
            var pers = p.Persistence > 1e-6f ? p.Persistence : 0.5f;
            var oct = (uint)Math.Clamp(p.Octaves, 1, 16);
            var baseF = p.BaseNoiseFrequency > 1e-6f ? p.BaseNoiseFrequency : 4f;

            if (!p.LandTypeBiomeNoiseDisabled)
                return false;

            var gpu = new NoiseParamsGpu
            {
                Width = (uint)width,
                Height = (uint)height,
                Seed = seed,
                Octaves = oct,
                Lacunarity = lac,
                Persistence = pers,
                BaseFreq = baseF,
                Pad = 0f,
            };

            _noise.DispatchRidgedNoise(heights, width, height, gpu);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryHydraulicErosion(
        float[] heights,
        float[] depositAccum,
        float[] flowAccum,
        int width,
        int height,
        uint seed,
        in ErosionParams p,
        Action<int, int, float[]>? afterPass = null)
    {
        try
        {
            int count = checked(width * height);
            if (heights.Length != count || depositAccum.Length != count || flowAccum.Length != count)
            {
                _hydrologyFromGpuHydrated = false;
                return false;
            }

            int passes = Math.Max(1, p.HydraulicPasses);
            uint drops = (uint)HydraulicDropsPerPassLimits.Clamp(p.HydraulicDropsPerPass);

            _hydraulic.ResetHydrologyAccumulation();
            for (int pass = 0; pass < passes; pass++)
            {
                var gpu = HydraulicGpu.BuildParams(width, height, seed, (uint)pass, drops, in p);
                _hydraulic.RunPass(heights, depositAccum, flowAccum, width, height, in gpu);
                afterPass?.Invoke(pass + 1, passes, heights);
            }

            ReadOnlySpan<int> hydFixed = _hydraulic.GetHydrologyFixedRowMajor(count);
            if (hydFixed.Length != count)
            {
                _hydrologyFromGpuHydrated = false;
                return false;
            }

            int maxI = 0;
            for (int i = 0; i < count; i++)
            {
                int v = hydFixed[i];
                if (v > maxI)
                    maxI = v;
            }

            if (_gpuHydrologyNormalized is null || _gpuHydrologyNormalized.Length != count)
                _gpuHydrologyNormalized = new float[count];

            if (maxI > 0)
            {
                float inv = 1f / maxI;
                for (int i = 0; i < count; i++)
                    _gpuHydrologyNormalized[i] = hydFixed[i] * inv;
            }
            else
                _gpuHydrologyNormalized.AsSpan().Clear();

            _hydrologyW = width;
            _hydrologyH = height;
            _hydrologyFromGpuHydrated = true;
            return true;
        }
        catch
        {
            _hydrologyFromGpuHydrated = false;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Vulkan bilateral SPIR-V is not implemented yet — the API falls back to the CPU bilateral filter.</remarks>
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
        if (_shallowWater is null || width < 8 || height < 8
            || terrainNormalizedRowMajor.Length != checked(width * height))
            return false;

        try
        {
            var resolved = TerrainGenerator.ResolveErosionParams(options?.ErosionTuning ?? default);
            var gpuParams = ShallowWaterGpu.BuildParams(width, height, in resolved);

            bool wantBiome = options?.IncludeBiomeRgbaPng == true && _calculateBiomes is not null;
            float[]? waterScratch = wantBiome ? new float[width * height] : null;

            float[]? flowScratch = wantBiome ? new float[width * height] : null;

            if (!_shallowWater.TrySimulateFlowMapPng(
                    terrainNormalizedRowMajor,
                    width,
                    height,
                    in gpuParams,
                    initialWaterDepth,
                    iterations,
                    waterScratch,
                    wantBiome ? flowScratch : null,
                    out pngGray16)
                || pngGray16 is null)
                return false;

            if (!wantBiome || waterScratch is null || flowScratch is null || options is null)
                return true;

            int count = width * height;

            if (_hydrologyFromGpuHydrated
                && width == _hydrologyW
                && height == _hydrologyH
                && _gpuHydrologyNormalized is not null
                && _gpuHydrologyNormalized.Length == count)
                _gpuHydrologyNormalized.AsSpan().CopyTo(waterScratch);

            var slope = new float[count];
            BiomeComputeGpu.ComputeSlopeDegrees(
                terrainNormalizedRowMajor,
                width,
                height,
                resolved.CellSizeMeters,
                resolved.MaxElevationMeters,
                slope);

            float maxW = 0f;
            for (int i = 0; i < count; i++)
            {
                float v = flowScratch[i];
                if (v > maxW)
                    maxW = v;
            }

            float flowInvMax = maxW > 1e-9f ? 1f / maxW : 0f;

            var biomeSets = TerrainBiomapPass.Resolved(resolved.Biomap);
            float snowLine = Math.Clamp(options.SnowLineMeters, 100f, 12_000f);
            uint noiseSeed =
                unchecked(
                    options.BiomeScatterNoiseSeed ^ (uint)(width * 374761393) ^ ((uint)(height << 17)));

            var bioUniform = BiomeParamsGpuMarshal.Build(
                width,
                height,
                snowLine,
                in resolved,
                biomeSets,
                flowInvMax,
                noiseSeed);

            return _calculateBiomes!.TryDispatchAndEncodePng(
                terrainNormalizedRowMajor,
                waterScratch,
                slope,
                flowScratch,
                width,
                height,
                bioUniform,
                out biomeRgbaPng);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _calculateBiomes?.Dispose();
        _shallowWater?.Dispose();
        _hydraulic.Dispose();
        _noise.Dispose();
        _ctx.Dispose();
        GC.SuppressFinalize(this);
    }
}
