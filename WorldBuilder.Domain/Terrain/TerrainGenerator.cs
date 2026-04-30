using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Procedural heightmap pipeline for a fixed rectangular domain (default 1024² ≈ 10 km × 10 km cell spacing via <see cref="ErosionParams.CellSizeMeters"/>).
/// Base: optional Simplex blend → heights … → umbrella smooth → soft-box outer safe-zone vignette → min–max scale to meters.
/// </summary>
public sealed class TerrainGenerator
{
    /// <summary>Default grid extent per axis (1024 × 1024).</summary>
    public const int DefaultResolution = 1024;

    /// <summary>Generate using <see cref="DefaultResolution"/> and default erosion tuning.</summary>
    public TerrainGenerationResult Generate(uint seed) =>
        Generate(DefaultResolution, DefaultResolution, seed, default);

    /// <summary>
    /// Flat row-major buffer → rectangular heightmap [row Y, column X], scaled to meters and splat masks.
    /// Pass <paramref name="erosionParams"/> as <c>default</c> to use <see cref="ErosionParams.Default1024Map"/>.
    /// </summary>
    /// <param name="retainFlatHeightSamplesForExport">
    /// When true, duplicates the internal row-major meter heights buffer so callers can encode PNG/raw via <see cref="TerrainHeightmapPngEncoder"/> without flattening <see cref="TerrainGenerationResult.HeightsMeters"/>.
    /// </param>
    public TerrainGenerationResult Generate(
        int width,
        int height,
        uint seed,
        ErosionParams erosionParams = default,
        TerrainGenerationCallbacks? callbacks = null,
        bool retainFlatHeightSamplesForExport = false,
        ITerrainGpuCompute? gpuCompute = null)
    {
        if (width < 4 || height < 4)
            throw new ArgumentOutOfRangeException(nameof(width));

        var resolved = ResolveErosionParams(erosionParams);

        var heights = new float[checked(width * height)];
        if (!resolved.LandTypeBiomeNoiseDisabled)
            TerrainNoise.FillLandBiomeBlendedHeightfield(heights, width, height, seed, resolved);
        else if (gpuCompute is { IsAvailable: true }
            && gpuCompute.TryFillRidgedMultifractal(heights, width, height, seed, resolved))
        {
            float maxR = TerrainNoise.ComputeRidgeMaximum(heights);
            TerrainNoise.NormalizeRidgeBufferByMax(heights, maxR);
        }
        else
            TerrainNoise.FillRidgedMultifractal(heights, width, height, seed, resolved);

        // Power curve squashing (plains/basins) after masked ridge+fBm composition — must precede tectonics / erosion.
        ApplyNoiseRedistribution(heights.AsSpan(), resolved.HeightPower);

        callbacks?.AfterNoiseFilled?.Invoke(heights);

        if (resolved.Tectonics.Enabled)
        {
            TerrainTectonicPass.Apply(heights, width, height, resolved.Tectonics, seed);
            callbacks?.AfterTectonicIteration?.Invoke(1, 1, heights);
        }

        ApplyThermalErosion(heights, width, height, resolved, callbacks?.AfterThermalIteration);

        var depositAccum = new float[heights.Length];
        var flowAccum = new float[heights.Length];
        if (gpuCompute is { IsAvailable: true }
            && gpuCompute.TryHydraulicErosion(heights, depositAccum, flowAccum, width, height, seed, resolved, callbacks?.AfterHydraulicPass))
        {
        }
        else
            ApplyHydraulicErosion(
                heights,
                depositAccum,
                flowAccum,
                width,
                height,
                seed,
                resolved,
                callbacks?.AfterHydraulicPass);

        int thermalRelaxAfter = Math.Clamp(resolved.ThermalRelaxIterationsAfterHydraulic, 0, 512);
        if (thermalRelaxAfter > 0)
            ApplyThermalErosion(heights, width, height, resolved, callbacks?.AfterThermalRelaxIteration, thermalRelaxAfter);

        byte[,] lakeSinkMask;
        if (resolved.Glacier.Enabled)
        {
            TerrainGlacierPass.Apply(heights, width, height, resolved.Glacier, seed, callbacks?.AfterGlacierIteration);
            TerrainHydrologicalSinkPass.Apply(heights, width, height, out lakeSinkMask);
            callbacks?.AfterHydrologicalSinkIteration?.Invoke(1, 1, heights);
        }
        else
            lakeSinkMask = new byte[height, width];

        PostProcessEdges(heights.AsSpan(), width, height, resolved.CellSizeMeters);

        TryApplyEdgePreservingBilateralSmooth(heights, width, height, in resolved, gpuCompute);

        TerrainLowSlopeSmoothing.Apply(heights, width, height, resolved.CellSizeMeters, resolved.MaxElevationMeters);

        ApplyMapEdgeSafeZoneVignette(heights.AsSpan(), width, height, resolved.CellSizeMeters, resolved.MapEdgeSafeZoneFalloffMeters);

        ScaleNormalizedHeightsToMeters(heights, resolved.MaxElevationMeters);

        ReadOnlyMemory<float>? flatExport = null;
        if (retainFlatHeightSamplesForExport)
        {
            var clone = new float[heights.Length];
            heights.AsSpan().CopyTo(clone);
            flatExport = clone.AsMemory();
        }

        var rectangular = ToRectangular(heights, width, height);
        ReadOnlySpan<float> meterSpan = heights;
        var splat = TerrainSplatmask.Create(
            meterSpan,
            depositAccum,
            flowAccum,
            width,
            height,
            resolved.CellSizeMeters,
            resolved.SeaLevelMeters,
            lakeSinkMask: resolved.Glacier.Enabled ? lakeSinkMask : null);

        byte[]? biomap =
            EncodeBiomapIfEnabled(
                meterSpan,
                depositAccum,
                flowAccum,
                resolved.Glacier.Enabled ? lakeSinkMask : null,
                width,
                height,
                resolved.CellSizeMeters,
                resolved.SeaLevelMeters,
                unchecked(seed ^ 0x814C_932Du),
                resolved.Biomap);

        byte[]? vegScatter =
            EncodeScatterVegetationIfEnabled(
                meterSpan,
                flowAccum,
                resolved.Glacier.Enabled ? lakeSinkMask : null,
                width,
                height,
                resolved.CellSizeMeters,
                resolved.SeaLevelMeters,
                unchecked(seed ^ 0x6D9D_71C3u),
                resolved.ScatterVegetation,
                resolved.Biomap);

        return new TerrainGenerationResult(rectangular, splat, flatExport, biomap, vegScatter);
    }

    /// <summary>
    /// Applies thermal relaxation + hydraulic erosion to heights decoded from an encoded preview (normalized row-major samples),
    /// without regenerating base noise.
    /// </summary>
    public TerrainGenerationResult ContinueErosion(
        ReadOnlySpan<float> normalizedHeightsRowMajor,
        int width,
        int height,
        ErosionParams erosionParams,
        uint hydraulicSeed,
        TerrainGenerationCallbacks? callbacks = null,
        bool retainFlatHeightSamplesForExport = false,
        ITerrainGpuCompute? gpuCompute = null,
        bool skipInitialThermalIteration = false)
    {
        if (width < 4 || height < 4)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (normalizedHeightsRowMajor.Length != checked(width * height))
            throw new ArgumentException("Span length must equal width × height.", nameof(normalizedHeightsRowMajor));

        var resolved = ResolveErosionParams(erosionParams);
        var heights = normalizedHeightsRowMajor.ToArray();

        if (!skipInitialThermalIteration)
            ApplyThermalErosion(heights, width, height, resolved, callbacks?.AfterThermalIteration);

        var depositAccum = new float[heights.Length];
        var flowAccum = new float[heights.Length];
        if (gpuCompute is { IsAvailable: true }
            && gpuCompute.TryHydraulicErosion(heights, depositAccum, flowAccum, width, height, hydraulicSeed, resolved, callbacks?.AfterHydraulicPass))
        {
        }
        else
            ApplyHydraulicErosion(
                heights,
                depositAccum,
                flowAccum,
                width,
                height,
                hydraulicSeed,
                resolved,
                callbacks?.AfterHydraulicPass);

        int thermalRelaxAfter = Math.Clamp(resolved.ThermalRelaxIterationsAfterHydraulic, 0, 512);
        if (thermalRelaxAfter > 0)
            ApplyThermalErosion(heights, width, height, resolved, callbacks?.AfterThermalRelaxIteration, thermalRelaxAfter);

        byte[,] lakeSinkMask;
        if (resolved.Glacier.Enabled)
        {
            TerrainGlacierPass.Apply(heights, width, height, resolved.Glacier, hydraulicSeed, callbacks?.AfterGlacierIteration);
            TerrainHydrologicalSinkPass.Apply(heights, width, height, out lakeSinkMask);
            callbacks?.AfterHydrologicalSinkIteration?.Invoke(1, 1, heights);
        }
        else
            lakeSinkMask = new byte[height, width];

        PostProcessEdges(heights.AsSpan(), width, height, resolved.CellSizeMeters);

        TryApplyEdgePreservingBilateralSmooth(heights, width, height, in resolved, gpuCompute);

        TerrainLowSlopeSmoothing.Apply(heights, width, height, resolved.CellSizeMeters, resolved.MaxElevationMeters);

        ApplyMapEdgeSafeZoneVignette(heights.AsSpan(), width, height, resolved.CellSizeMeters, resolved.MapEdgeSafeZoneFalloffMeters);

        ScaleNormalizedHeightsToMeters(heights, resolved.MaxElevationMeters);

        ReadOnlyMemory<float>? flatExport = null;
        if (retainFlatHeightSamplesForExport)
        {
            var clone = new float[heights.Length];
            heights.AsSpan().CopyTo(clone);
            flatExport = clone.AsMemory();
        }

        var rectangular = ToRectangular(heights, width, height);
        ReadOnlySpan<float> meterSpan = heights;
        var splat = TerrainSplatmask.Create(
            meterSpan,
            depositAccum,
            flowAccum,
            width,
            height,
            resolved.CellSizeMeters,
            resolved.SeaLevelMeters,
            lakeSinkMask: resolved.Glacier.Enabled ? lakeSinkMask : null);

        byte[]? biomapContinue =
            EncodeBiomapIfEnabled(
                meterSpan,
                depositAccum,
                flowAccum,
                resolved.Glacier.Enabled ? lakeSinkMask : null,
                width,
                height,
                resolved.CellSizeMeters,
                resolved.SeaLevelMeters,
                unchecked(hydraulicSeed ^ 0x814C_932Du),
                resolved.Biomap);

        byte[]? vegScatterContinue =
            EncodeScatterVegetationIfEnabled(
                meterSpan,
                flowAccum,
                resolved.Glacier.Enabled ? lakeSinkMask : null,
                width,
                height,
                resolved.CellSizeMeters,
                resolved.SeaLevelMeters,
                unchecked(hydraulicSeed ^ 0x6D9D_71C3u),
                resolved.ScatterVegetation,
                resolved.Biomap);

        return new TerrainGenerationResult(rectangular, splat, flatExport, biomapContinue, vegScatterContinue);
    }

    private static byte[]? EncodeBiomapIfEnabled(
        ReadOnlySpan<float> heightsMetersRowMajor,
        ReadOnlySpan<float> depositAccum,
        ReadOnlySpan<float> flowAccum,
        byte[,]? lakeSinkMask,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        uint biomapNoiseSeed,
        BiomapGenerationSettings biome)
    {
        if (!biome.Enabled)
            return null;

        return TerrainBiomapPass.EncodeBiomapRgbaPng(
            heightsMetersRowMajor,
            depositAccum,
            flowAccum,
            lakeSinkMask,
            width,
            height,
            cellSizeMeters,
            seaLevelMeters,
            biomapNoiseSeed,
            biome);
    }

    private static byte[]? EncodeScatterVegetationIfEnabled(
        ReadOnlySpan<float> heightsMetersRowMajor,
        ReadOnlySpan<float> flowAccum,
        byte[,]? lakeSinkMask,
        int width,
        int height,
        float cellSizeMeters,
        float seaLevelMeters,
        uint scatterNoiseSeed,
        VegetationScatterGenerationSettings scatter,
        BiomapGenerationSettings biomapBlurSource)
    {
        if (!scatter.Enabled)
            return null;

        return TerrainVegetationScatterPass.EncodeScatterMaskPng(
            heightsMetersRowMajor,
            flowAccum,
            lakeSinkMask,
            width,
            height,
            cellSizeMeters,
            seaLevelMeters,
            scatterNoiseSeed,
            scatter,
            biomapBlurSource,
            biomapBlurSource.SnowLineMeters);
    }

    public static ErosionParams ResolveErosionParams(ErosionParams erosionParams = default)
    {
        // Default struct has CellSizeMeters == 0 — swap in curated defaults for a 1024² world.
        if (erosionParams.CellSizeMeters <= 0f || erosionParams.Octaves <= 0)
            return ErosionParams.Default1024Map;

        if (erosionParams.MaxElevationMeters <= 0f)
            erosionParams = erosionParams with { MaxElevationMeters = 2000f };

        if (erosionParams.HeightPower <= 0f)
            erosionParams = erosionParams with { HeightPower = 2.2f };

        if (erosionParams.TerrainTypeScale <= 0f)
            erosionParams = erosionParams with { TerrainTypeScale = 0.0001f };

        if (erosionParams.PlainsPersistence <= 0f)
            erosionParams = erosionParams with { PlainsPersistence = 0.3f };

        float mt = erosionParams.MountainThreshold;
        if (mt <= 0f)
            mt = 0.6f;
        mt = Math.Clamp(mt, 0.02f, 0.98f);
        erosionParams = erosionParams with { MountainThreshold = mt };

        erosionParams = erosionParams with
        {
            SmoothingStrength = Math.Clamp(erosionParams.SmoothingStrength, 0f, 4f),
            SmoothingRadius = Math.Clamp(erosionParams.SmoothingRadius, 0f, 32f),
        };

        float depSigma = erosionParams.HydraulicDepositDistributionSigmaPx;
        if (depSigma <= 0f)
            depSigma = 0.55f;
        depSigma = Math.Clamp(depSigma, 0.08f, 2f);

        erosionParams = erosionParams with { HydraulicDepositDistributionSigmaPx = depSigma };

        float zone = erosionParams.MapEdgeSafeZoneFalloffMeters;
        if (zone < 0f)
            zone = 0f;
        else if (zone > 0f)
            zone = Math.Clamp(zone, 1f, 8000f);
        erosionParams = erosionParams with { MapEdgeSafeZoneFalloffMeters = zone };

        erosionParams = erosionParams with
        {
            Biomap = TerrainBiomapPass.Resolved(erosionParams.Biomap),
            ScatterVegetation = TerrainVegetationScatterPass.Resolved(erosionParams.ScatterVegetation),
        };

        return erosionParams;
    }

    /// <summary>
    /// Soft rectangular vignette on normalized heights: distance to nearest map edge drives a smooth Hermite ramp from zero (at the edge) to one (beyond <paramref name="falloffMeters"/>).
    /// </summary>
    private static void ApplyMapEdgeSafeZoneVignette(
        Span<float> heights,
        int width,
        int height,
        float cellSizeMeters,
        float falloffMeters)
    {
        if (width < 2 || height < 2 || heights.Length != checked(width * height) || falloffMeters <= 1e-6f)
            return;

        float cell = Math.Max(cellSizeMeters, 1e-6f);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float dMeterY = Math.Min(y, height - 1 - y) * cell;

            for (int x = 0; x < width; x++)
            {
                float dMeterX = Math.Min(x, width - 1 - x) * cell;
                float dMeters = Math.Min(dMeterX, dMeterY);
                float m = SmoothStep01(0f, falloffMeters, dMeters);
                heights[row + x] *= m;
            }
        }
    }

    private static float SmoothStep01(float edge0, float edge1, float x)
    {
        if (x <= edge0)
            return 0f;
        if (x >= edge1)
            return 1f;
        float t = (x - edge0) / (edge1 - edge0);
        return t * t * (3f - 2f * t);
    }

    /// <summary><c>height = pow(height, heightPower)</c> on normalized samples (no-op when factor ≤ 0 or ≈ 1).</summary>
    private static void ApplyNoiseRedistribution(Span<float> heights, float factor)
    {
        if (factor <= 0f || Math.Abs(factor - 1f) < 1e-8f)
            return;

        for (int i = 0; i < heights.Length; i++)
        {
            float h = Math.Clamp(heights[i], 0f, 1f);
            heights[i] = MathF.Pow(h, factor);
        }
    }

    /// <summary>
    /// Radial-box edge mask: weight is 1.0 in the interior and ramps linearly to 0 at the boundary over a 512&nbsp;m falloff zone (from cell spacing).
    /// Applied after all erosion so the 10&nbsp;km domain edge forms a flat, traversable skirt instead of cliff artifacts.
    /// </summary>
    private static void PostProcessEdges(Span<float> heights, int width, int height, float cellSizeMeters)
    {
        if (width < 4 || height < 4 || heights.Length < checked(width * height))
            return;

        float cell = Math.Max(cellSizeMeters, 1e-6f);
        const float falloffMeters = 512f;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float dPx = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y));
                float dMeters = dPx * cell;
                float m = dMeters >= falloffMeters ? 1f : dMeters / falloffMeters;
                heights[row + x] *= m;
            }
        }
    }

    /// <summary>
    /// Optional edge-preserving bilateral filter on normalized heights. GPU hook reserved; Vulkan path currently falls back here.
    /// </summary>
    private static void TryApplyEdgePreservingBilateralSmooth(
        float[] heights,
        int width,
        int height,
        in ErosionParams p,
        ITerrainGpuCompute? gpuCompute)
    {
        if (p.SmoothingStrength <= 0f || p.SmoothingRadius < 1f)
            return;

        if (gpuCompute is { IsAvailable: true }
            && gpuCompute.TryEdgePreservingBilateralSmooth(heights, width, height, in p))
            return;

        TerrainEdgePreservingBilateralSmooth.Apply(heights, width, height, p.SmoothingStrength, p.SmoothingRadius);
    }

    private static void ScaleNormalizedHeightsToMeters(Span<float> heights, float maxElevationMeters)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < heights.Length; i++)
        {
            float v = heights[i];
            if (v < min)
                min = v;
            if (v > max)
                max = v;
        }

        float range = Math.Max(max - min, 1e-12f);
        float scale = maxElevationMeters / range;
        for (int i = 0; i < heights.Length; i++)
            heights[i] = (heights[i] - min) * scale;
    }

    private static float[,] ToRectangular(ReadOnlySpan<float> flat, int width, int height)
    {
        var result = new float[height, width];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
                result[y, x] = flat[row + x];
        }

        return result;
    }

    /// <summary>Thermal talus: paired-grid edges transfer downhill (mass conserving). Reads prior heights only; phases parallelize disjoint edges.</summary>
    /// <param name="iterationCountOverride">When set, runs exactly this many iterations instead of <see cref="ErosionParams.ThermalIterations"/>.</param>
    private static void ApplyThermalErosion(
        float[] h,
        int width,
        int height,
        in ErosionParams p,
        Action<int, int, float[]>? afterIteration,
        int? iterationCountOverride = null)
    {
        float maxStep = Math.Max(1e-8f, p.ThermalMaxStep);
        float rate = Math.Clamp(p.ThermalStrength, 0f, 1f);
        int iterations = Math.Max(1, iterationCountOverride ?? p.ThermalIterations);
        var delta = new float[h.Length];

        float[] prevBuf = h;
        float[] curBuf = new float[h.Length];

        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Fill(delta, 0f);

            AccumulateThermalHorizontalEdges(prevBuf, delta, width, height, maxStep, rate, evenColumns: true);
            AccumulateThermalHorizontalEdges(prevBuf, delta, width, height, maxStep, rate, evenColumns: false);
            AccumulateThermalVerticalEdges(prevBuf, delta, width, height, maxStep, rate, evenRows: true);
            AccumulateThermalVerticalEdges(prevBuf, delta, width, height, maxStep, rate, evenRows: false);

            Parallel.For(0, h.Length, i => curBuf[i] = Math.Max(0f, prevBuf[i] + delta[i]));

            afterIteration?.Invoke(iter + 1, iterations, curBuf);

            (prevBuf, curBuf) = (curBuf, prevBuf);
        }

        if (!ReferenceEquals(prevBuf, h))
            Array.Copy(prevBuf, h, h.Length);
    }

    private static void AccumulateThermalHorizontalEdges(float[] prev, float[] delta, int width, int height, float maxStep, float rate, bool evenColumns)
    {
        Parallel.For(1, height - 1, y =>
        {
            int row = y * width;
            int xStart = evenColumns ? 0 : 1;
            for (int x = xStart; x < width - 1; x += 2)
                AccumulateTalusDelta(prev, delta, row + x, row + x + 1, maxStep, rate);
        });
    }

    private static void AccumulateThermalVerticalEdges(float[] prev, float[] delta, int width, int height, float maxStep, float rate, bool evenRows)
    {
        if (evenRows)
        {
            int count = Math.Max(0, (height - 2) / 2 + 1);
            Parallel.For(0, count, k =>
            {
                int y = k * 2;
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                    AccumulateTalusDelta(prev, delta, row + x, row + x + width, maxStep, rate);
            });
        }
        else
        {
            int count = Math.Max(0, (height - 3) / 2 + 1);
            Parallel.For(0, count, k =>
            {
                int y = k * 2 + 1;
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                    AccumulateTalusDelta(prev, delta, row + x, row + x + width, maxStep, rate);
            });
        }
    }

    private static void AccumulateTalusDelta(float[] prev, float[] delta, int a, int b, float maxStep, float rate)
    {
        float dh = prev[a] - prev[b];
        if (dh > maxStep)
        {
            float move = rate * (dh - maxStep) * 0.5f;
            delta[a] -= move;
            delta[b] += move;
        }
        else if (dh < -maxStep)
        {
            float move = rate * (-dh - maxStep) * 0.5f;
            delta[b] -= move;
            delta[a] += move;
        }
    }

    private static void ApplyHydraulicErosion(
        float[] h,
        float[] depositAccum,
        float[] flowAccum,
        int width,
        int height,
        uint seed,
        in ErosionParams p,
        Action<int, int, float[]>? afterPass)
    {
        int passes = Math.Max(1, p.HydraulicPasses);
        int dropsPerPass = HydraulicDropsPerPassLimits.Clamp(p.HydraulicDropsPerPass);
        int lifetime = Math.Clamp(p.DropletMaxLifetime, 4, 512);
        float inertia = Math.Clamp(p.DropletInertia, 0f, 1f);
        float invInertia = 1f - inertia;
        float capFactor = Math.Max(1e-6f, p.SedimentCapacityFactor);
        float hydraulicCapLimit = p.HydraulicCapacityLimit > 0f ? Math.Clamp(p.HydraulicCapacityLimit, 0f, 1f) : 0f;
        float maxSediment = p.MaxSediment > 0f ? p.MaxSediment : 0f;
        float depositRatio = Math.Clamp(p.DepositRatio, 0f, 1f);
        float erodeRatio = Math.Clamp(p.ErodeRatio, 0f, 1f);
        float retention = Math.Clamp(p.WaterRetention, 0f, 1f);
        float accel = Math.Max(0f, p.DropletAcceleration);
        float maxElev = Math.Max(p.MaxElevationMeters, 1f);
        float cellSize = Math.Max(p.CellSizeMeters, 1e-6f);
        float hydDepositSigmaPx = p.HydraulicDepositDistributionSigmaPx;

        float maxX = width - 1.000001f;
        float maxY = height - 1.000001f;

        var snapshot = new float[h.Length];

        for (int pass = 0; pass < passes; pass++)
        {
            Buffer.BlockCopy(h, 0, snapshot, 0, sizeof(float) * h.Length);

            var bag = new ConcurrentBag<HydraulicScratch>();

            Parallel.For(
                0,
                dropsPerPass,
                () => new HydraulicScratch(h.Length),
                (d, _, scratch) =>
                {
                    ulong rng = TerrainNoise.SplitMix64(seed ^ 0xC001D00DCAFEUL ^ ((ulong)(uint)pass << 32 | (ulong)(uint)d));
                    RunHydraulicDroplet(
                        snapshot,
                        scratch.HeightDelta,
                        scratch.DepositDelta,
                        scratch.FlowDelta,
                        width,
                        height,
                        ref rng,
                        lifetime,
                        inertia,
                        invInertia,
                        capFactor,
                        hydraulicCapLimit,
                        maxSediment,
                        depositRatio,
                        erodeRatio,
                        retention,
                        accel,
                        maxElev,
                        cellSize,
                        hydDepositSigmaPx,
                        maxX,
                        maxY);
                    return scratch;
                },
                scratch => bag.Add(scratch));

            foreach (var scratch in bag)
                MergeHydraulicScratch(h, depositAccum, flowAccum, scratch);

            afterPass?.Invoke(pass + 1, passes, h);
        }
    }

    private sealed class HydraulicScratch
    {
        public readonly float[] HeightDelta;
        public readonly float[] DepositDelta;
        public readonly float[] FlowDelta;

        public HydraulicScratch(int len)
        {
            HeightDelta = new float[len];
            DepositDelta = new float[len];
            FlowDelta = new float[len];
        }
    }

    private static void MergeHydraulicScratch(float[] h, float[] depositAccum, float[] flowAccum, HydraulicScratch scratch)
    {
        int len = h.Length;
        for (int i = 0; i < len; i++)
        {
            float dh = scratch.HeightDelta[i];
            if (dh != 0f)
            {
                float v = h[i] + dh;
                if (v < 0f)
                    v = 0f;
                h[i] = v;
            }

            depositAccum[i] += scratch.DepositDelta[i];
            flowAccum[i] += scratch.FlowDelta[i];
        }
    }

    private static void RunHydraulicDroplet(
        ReadOnlySpan<float> terrainSnapshot,
        Span<float> heightDelta,
        Span<float> depositDelta,
        Span<float> flowDelta,
        int width,
        int height,
        ref ulong rng,
        int lifetime,
        float inertia,
        float invInertia,
        float capFactor,
        float hydraulicCapacityLimit,
        float maxSediment,
        float depositRatio,
        float erodeRatio,
        float retention,
        float accel,
        float maxElevationMeters,
        float cellSizeMeters,
        float hydraulicDepositGaussianSigmaPx,
        float maxX,
        float maxY)
    {
        float fx = TerrainNoise.NextFloat01(ref rng) * maxX;
        float fy = TerrainNoise.NextFloat01(ref rng) * maxY;

        float vx = 0f;
        float vy = 0f;
        float sediment = 0f;
        float water = 1f;

        for (int step = 0; step < lifetime && water > 1e-4f; step++)
        {
            fx = Math.Clamp(fx, 0f, maxX);
            fy = Math.Clamp(fy, 0f, maxY);

            int xi = (int)MathF.Floor(fx);
            int yi = (int)MathF.Floor(fy);
            if (xi <= 1 || xi >= width - 2 || yi <= 1 || yi >= height - 2)
                break;

            ApplyBilinearWeightedAdd(flowDelta, width, height, fx, fy, water);

            float gx = (SampleBilinear(terrainSnapshot, width, height, fx + 1f, fy) -
                        SampleBilinear(terrainSnapshot, width, height, fx - 1f, fy)) * 0.5f;
            float gy = (SampleBilinear(terrainSnapshot, width, height, fx, fy + 1f) -
                        SampleBilinear(terrainSnapshot, width, height, fx, fy - 1f)) * 0.5f;

            float nx = -gx * accel * invInertia * water;
            float ny = -gy * accel * invInertia * water;
            vx = inertia * vx + nx;
            vy = inertia * vy + ny;

            const float maxVel = 3f;
            float vmag = MathF.Sqrt(vx * vx + vy * vy);
            if (vmag > maxVel && vmag > 1e-6f)
            {
                float s = maxVel / vmag;
                vx *= s;
                vy *= s;
                vmag = maxVel;
            }

            float slopeMag = MathF.Sqrt(gx * gx + gy * gy);
            float capacity = Math.Max(0f, vmag * slopeMag * capFactor);
            if (maxSediment > 0f)
                capacity = Math.Min(capacity, maxSediment);

            float depositRatioEffective = HydraulicDepositRatioWithLowlandBoost(
                depositRatio,
                vmag,
                slopeMag,
                maxElevationMeters,
                cellSizeMeters);

            if (sediment > capacity)
            {
                float depositAmt = depositRatioEffective * (sediment - capacity);
                sediment -= depositAmt;
                AccumulateHydraulicGaussianDeposit(
                    heightDelta,
                    depositDelta,
                    width,
                    height,
                    fx,
                    fy,
                    depositAmt,
                    hydraulicDepositGaussianSigmaPx);
            }
            else
            {
                float deficit = capacity - sediment;
                float erodeAmt = erodeRatio * deficit;
                float hLocal = SampleBilinear(terrainSnapshot, width, height, fx, fy);
                float maxByHeight = hydraulicCapacityLimit > 0f
                    ? hydraulicCapacityLimit * hLocal
                    : float.MaxValue;
                erodeAmt = Math.Min(erodeAmt, maxByHeight);
                AddBilinearDeltaUnchecked(heightDelta, width, height, fx, fy, -erodeAmt);
                sediment += erodeAmt;
            }

            water *= retention;

            fx += vx;
            fy += vy;

            if (fx < 0f || fy < 0f || fx > maxX || fy > maxY)
                break;
        }
    }

    /// <summary>
    /// Boosts deposition on gentle slopes (&lt; ~5° surface tilt) and when velocity is low — favors aggradation at ridge toes / plains.
    /// </summary>
    private static float HydraulicDepositRatioWithLowlandBoost(
        float depositRatio,
        float vmag,
        float slopeMagGrad,
        float maxElevationMeters,
        float cellSizeMeters)
    {
        const float slopeDegThreshold = 5f;
        float tanSlopeThreshold = MathF.Tan(slopeDegThreshold * (MathF.PI / 180f));
        const float lowVelReference = 1f;
        const float boostStrength = 2f;

        float tanSlopeApprox = slopeMagGrad * maxElevationMeters / cellSizeMeters;
        float gentleSlopeBlend = tanSlopeApprox < tanSlopeThreshold
            ? 1f - tanSlopeApprox / tanSlopeThreshold
            : 0f;

        float lowVelBlend = vmag < lowVelReference ? 1f - vmag / lowVelReference : 0f;

        float blend = Math.Max(gentleSlopeBlend, lowVelBlend);
        float depositBoost = 1f + boostStrength * blend;
        return Math.Clamp(depositRatio * depositBoost, 0f, 1f);
    }

    /// <summary>
    /// Spread surplus sediment across a 3×3 neighborhood with Gaussian weights (sub-pixel center), summing to <paramref name="depositAmt"/>.
    /// </summary>
    private static void AccumulateHydraulicGaussianDeposit(
        Span<float> heightDelta,
        Span<float> depositDelta,
        int width,
        int height,
        float fx,
        float fy,
        float depositAmt,
        float gaussianSigmaPx)
    {
        if (depositAmt <= 0f)
            return;

        fx = Math.Clamp(fx, 0f, width - 1f);
        fy = Math.Clamp(fy, 0f, height - 1f);

        float sigma = Math.Max(gaussianSigmaPx, 0.08f);
        float twoSigmaSq = 2f * sigma * sigma;

        int cx = Math.Clamp((int)MathF.Floor(fx + 0.5f), 1, width - 2);
        int cy = Math.Clamp((int)MathF.Floor(fy + 0.5f), 1, height - 2);

        Span<float> w = stackalloc float[9];
        float sum = 0f;
        int k = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++, k++)
            {
                int sx = cx + dx;
                int sy = cy + dy;
                float lx = fx - sx;
                float ly = fy - sy;
                float dist2 = lx * lx + ly * ly;
                w[k] = MathF.Exp(-dist2 / twoSigmaSq);
                sum += w[k];
            }
        }

        float inv = 1f / sum;
        k = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int row = (cy + dy) * width;
            for (int dx = -1; dx <= 1; dx++, k++)
            {
                float wt = w[k] * inv;
                int idx = row + cx + dx;
                float add = depositAmt * wt;
                heightDelta[idx] += add;
                depositDelta[idx] += add;
            }
        }
    }

    /// <summary>Bilinear splat without clamping corners — merges apply non-negativity once.</summary>
    private static void AddBilinearDeltaUnchecked(Span<float> h, int width, int height, float fx, float fy, float delta)
    {
        fx = Math.Clamp(fx, 0f, width - 1f);
        fy = Math.Clamp(fy, 0f, height - 1f);

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float tx = fx - x0;
        float ty = fy - y0;

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        int i00 = y0 * width + x0;
        int i10 = y0 * width + x1;
        int i01 = y1 * width + x0;
        int i11 = y1 * width + x1;

        h[i00] += delta * w00;
        h[i10] += delta * w10;
        h[i01] += delta * w01;
        h[i11] += delta * w11;
    }

    private static float SampleBilinear(ReadOnlySpan<float> h, int width, int height, float fx, float fy)
    {
        fx = Math.Clamp(fx, 0f, width - 1f);
        fy = Math.Clamp(fy, 0f, height - 1f);

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float tx = fx - x0;
        float ty = fy - y0;

        float h00 = h[y0 * width + x0];
        float h10 = h[y0 * width + x1];
        float h01 = h[y1 * width + x0];
        float h11 = h[y1 * width + x1];

        float ix0 = h00 + (h10 - h00) * tx;
        float ix1 = h01 + (h11 - h01) * tx;
        return ix0 + (ix1 - ix0) * ty;
    }

    private static void ApplyBilinearDelta(Span<float> h, int width, int height, float fx, float fy, float delta)
    {
        fx = Math.Clamp(fx, 0f, width - 1f);
        fy = Math.Clamp(fy, 0f, height - 1f);

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float tx = fx - x0;
        float ty = fy - y0;

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        int i00 = y0 * width + x0;
        int i10 = y0 * width + x1;
        int i01 = y1 * width + x0;
        int i11 = y1 * width + x1;

        h[i00] += delta * w00;
        h[i10] += delta * w10;
        h[i01] += delta * w01;
        h[i11] += delta * w11;

        ClampNonNegative(h, i00);
        ClampNonNegative(h, i10);
        ClampNonNegative(h, i01);
        ClampNonNegative(h, i11);
    }

    private static void ApplyBilinearWeightedAdd(Span<float> accum, int width, int height, float fx, float fy, float amount)
    {
        if (amount <= 0f)
            return;

        fx = Math.Clamp(fx, 0f, width - 1f);
        fy = Math.Clamp(fy, 0f, height - 1f);

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float tx = fx - x0;
        float ty = fy - y0;

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        int i00 = y0 * width + x0;
        int i10 = y0 * width + x1;
        int i01 = y1 * width + x0;
        int i11 = y1 * width + x1;

        accum[i00] += amount * w00;
        accum[i10] += amount * w10;
        accum[i01] += amount * w01;
        accum[i11] += amount * w11;
    }

    private static void ClampNonNegative(Span<float> h, int idx)
    {
        if (h[idx] < 0f)
            h[idx] = 0f;
    }
}
