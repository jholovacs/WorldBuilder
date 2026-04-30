using WorldBuilder.Domain.Terrain;
using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Terrain;

public static class TerrainComplianceEvaluator
{
    public static TerrainComplianceReport Evaluate(WorldDefinition world, IReadOnlyList<float> heights)
    {
        int n = heights.Count;
        if (n == 0)
            return Empty(world);

        double targetWater = world.SurfaceWaterCoveragePercent / 100.0;
        int belowOrEq = 0;
        var land = new List<float>();
        float minElev = float.PositiveInfinity;
        float maxElev = float.NegativeInfinity;

        foreach (var z in heights)
        {
            if (z <= 0f)
                belowOrEq++;
            else
                land.Add(z);

            if (z < minElev)
                minElev = z;
            if (z > maxElev)
                maxElev = z;
        }

        double actualWater = belowOrEq / (double)n;

        float medianLand = land.Count == 0 ? 0f : Median(land);
        float actualDepthBelowSea = Math.Max(0f, -minElev);

        bool wf = FractionWithinTol(actualWater, targetWater);
        bool mf = ScalarWithinTol(medianLand, (float)world.MedianGroundElevationAboveSeaLevelMeters);
        bool xf = ScalarWithinTol(maxElev, (float)world.MaxGroundElevationAboveSeaLevelMeters);
        bool df = ScalarWithinTol(actualDepthBelowSea, (float)world.MinGroundDepthBelowSeaLevelMeters);

        var all = wf && mf && xf && df;

        return new TerrainComplianceReport
        {
            TargetWaterFraction = targetWater,
            ActualWaterFraction = actualWater,
            WaterFractionWithinTolerance = wf,
            TargetMedianLandElevationMeters = world.MedianGroundElevationAboveSeaLevelMeters,
            ActualMedianLandElevationMeters = medianLand,
            MedianLandWithinTolerance = mf,
            TargetMaxElevationMeters = world.MaxGroundElevationAboveSeaLevelMeters,
            ActualMaxElevationMeters = maxElev,
            MaxElevationWithinTolerance = xf,
            TargetMinGroundDepthMeters = world.MinGroundDepthBelowSeaLevelMeters,
            ActualMaxDepthBelowSeaMeters = actualDepthBelowSea,
            MinDepthWithinTolerance = df,
            AllParametersWithinTolerance = all,
        };
    }

    private static TerrainComplianceReport Empty(WorldDefinition world)
    {
        return new TerrainComplianceReport
        {
            TargetWaterFraction = world.SurfaceWaterCoveragePercent / 100.0,
            ActualWaterFraction = 0,
            WaterFractionWithinTolerance = false,
            TargetMedianLandElevationMeters = world.MedianGroundElevationAboveSeaLevelMeters,
            ActualMedianLandElevationMeters = 0,
            MedianLandWithinTolerance = false,
            TargetMaxElevationMeters = world.MaxGroundElevationAboveSeaLevelMeters,
            ActualMaxElevationMeters = 0,
            MaxElevationWithinTolerance = false,
            TargetMinGroundDepthMeters = world.MinGroundDepthBelowSeaLevelMeters,
            ActualMaxDepthBelowSeaMeters = 0,
            MinDepthWithinTolerance = false,
            AllParametersWithinTolerance = false,
        };
    }

    private static float Median(List<float> sortedCandidate)
    {
        sortedCandidate.Sort();
        int m = sortedCandidate.Count;
        if (m == 0)
            return 0f;
        int mid = m / 2;
        return (m % 2 == 0)
            ? (sortedCandidate[mid - 1] + sortedCandidate[mid]) * 0.5f
            : sortedCandidate[mid];
    }

    private static bool FractionWithinTol(double actual, double target)
    {
        return Math.Abs(actual - target) <= TerrainGenerationLimits.SurfaceFractionTolerance;
    }

    private static bool ScalarWithinTol(float actual, float target)
    {
        double tol = TerrainGenerationLimits.ParameterComplianceTolerance;
        double denom = Math.Max(1e-6, Math.Abs(target));
        return Math.Abs(actual - target) / denom <= tol;
    }
}
