using System.Buffers;
using WorldBuilder.Domain.Terrain;
using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Terrain;

/// <summary>
/// Iterative uplift from a flat ocean floor: each pass picks 10–20% of tiles, applies a random 5–20 m core raise
/// with a 10–250 m radial falloff, until surface water fraction is within tolerance of the world target.
/// </summary>
public static class TerrainElevationGenerator
{
    private const float InnerRadiusMeters = 10f;

    private const float OuterRadiusMeters = 250f;

    private const float BumpMinMeters = 5f;

    private const float BumpMaxMeters = 20f;

    private const float PickFractionMin = 0.10f;

    private const float PickFractionMax = 0.20f;

    /// <summary>Upper bound on uplift iterations (deterministic termination).</summary>
    private const int MaxIterations = 500_000;

    private const int ProgressEveryIterations = 50;

    public static (float[] Heights, TerrainComplianceReport Report) Generate(
        WorldDefinition world,
        int gridWidth,
        int gridHeight,
        Action<TerrainElevationProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        int n = gridWidth * gridHeight;
        var heights = new float[n];

        float floorElev = -(float)world.MinGroundDepthBelowSeaLevelMeters;
        for (var i = 0; i < n; i++)
            heights[i] = floorElev;

        ReadOnlySpan<byte> wid = world.Id.ToByteArray();
        ulong ctr = 0;

        double targetWater = world.SurfaceWaterCoveragePercent / 100.0;
        float cellSize = (float)Math.Sqrt(Math.Max(world.GroundResolutionSquareMeters, 1e-6));

        double actualWater = ComputeWaterFraction(heights.AsSpan());
        int iteration = 0;

        void Report(bool completed)
        {
            onProgress?.Invoke(new TerrainElevationProgress(iteration, actualWater, targetWater, completed));
        }

        Report(completed: false);

        var pool = ArrayPool<int>.Shared.Rent(n);
        try
        {
            while (!WaterFractionWithinTol(actualWater, targetWater) && iteration < MaxIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                iteration++;
                RunIteration(heights.AsSpan(), gridWidth, gridHeight, wid, world.Seed, ref ctr, cellSize, pool, n);

                actualWater = ComputeWaterFraction(heights.AsSpan());

                if (iteration == 1 || iteration % ProgressEveryIterations == 0 || WaterFractionWithinTol(actualWater, targetWater))
                    Report(completed: false);
            }

            Report(completed: true);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pool);
        }

        var report = TerrainComplianceEvaluator.Evaluate(world, heights);
        return (heights, report);
    }

    private static void RunIteration(
        Span<float> heights,
        int gw,
        int gh,
        ReadOnlySpan<byte> wid,
        ulong seed,
        ref ulong ctr,
        float cellSize,
        int[] pool,
        int n)
    {
        float pickFrac = CryptoTerrainSampler.UniformFloatInclusive(wid, seed, ref ctr, PickFractionMin, PickFractionMax);
        int pickCount = Math.Clamp((int)Math.Round(pickFrac * n), 1, n);

        for (var i = 0; i < n; i++)
            pool[i] = i;

        for (var i = 0; i < pickCount; i++)
        {
            int j = CryptoTerrainSampler.UniformIntInclusive(wid, seed, ref ctr, i, n - 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int rCells = Math.Max(1, (int)Math.Ceiling(OuterRadiusMeters / Math.Max(cellSize, 1e-6f)));

        for (var p = 0; p < pickCount; p++)
        {
            int idx = pool[p];
            int sx = idx % gw;
            int sy = idx / gw;

            float bump = CryptoTerrainSampler.UniformFloatInclusive(wid, seed, ref ctr, BumpMinMeters, BumpMaxMeters);

            int x0 = Math.Max(0, sx - rCells);
            int x1 = Math.Min(gw - 1, sx + rCells);
            int y0 = Math.Max(0, sy - rCells);
            int y1 = Math.Min(gh - 1, sy + rCells);

                for (int iy = y0; iy <= y1; iy++)
                {
                    for (int ix = x0; ix <= x1; ix++)
                    {
                        float dx = (ix - sx) * cellSize;
                        float dy = (iy - sy) * cellSize;
                        float sq = dx * dx + dy * dy;
                        float w = RadialFalloffSquared(sq);
                        if (w <= 0f)
                            continue;

                        int j = iy * gw + ix;
                        heights[j] += bump * w;
                    }
                }
        }
    }

    /// <summary>Plateau inside inner radius; smooth decay from inner ring to zero at outer radius.</summary>
    private static float RadialFalloffSquared(float distanceSquared)
    {
        float outer2 = OuterRadiusMeters * OuterRadiusMeters;
        if (distanceSquared > outer2)
            return 0f;

        float inner2 = InnerRadiusMeters * InnerRadiusMeters;
        if (distanceSquared <= inner2)
            return 1f;

        float d = MathF.Sqrt(distanceSquared);
        float t = (OuterRadiusMeters - d) / (OuterRadiusMeters - InnerRadiusMeters);
        float u = t * t * (3f - 2f * t);
        return Math.Clamp(u, 0f, 1f);
    }

    private static double ComputeWaterFraction(ReadOnlySpan<float> heights)
    {
        if (heights.Length == 0)
            return 0;

        var below = 0;
        for (var i = 0; i < heights.Length; i++)
        {
            if (heights[i] <= 0f)
                below++;
        }

        return below / (double)heights.Length;
    }

    private static bool WaterFractionWithinTol(double actual, double target)
    {
        return Math.Abs(actual - target) <= TerrainGenerationLimits.SurfaceFractionTolerance;
    }
}
