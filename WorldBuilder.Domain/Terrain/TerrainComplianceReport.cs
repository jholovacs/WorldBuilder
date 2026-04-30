namespace WorldBuilder.Domain.Terrain;

/// <summary>Observed metrics after terrain synthesis vs world targets.</summary>
public sealed class TerrainComplianceReport
{
    public double TargetWaterFraction { get; init; }

    public double ActualWaterFraction { get; init; }

    public bool WaterFractionWithinTolerance { get; init; }

    public double TargetMedianLandElevationMeters { get; init; }

    public double ActualMedianLandElevationMeters { get; init; }

    public bool MedianLandWithinTolerance { get; init; }

    public double TargetMaxElevationMeters { get; init; }

    public double ActualMaxElevationMeters { get; init; }

    public bool MaxElevationWithinTolerance { get; init; }

    public double TargetMinGroundDepthMeters { get; init; }

    public double ActualMaxDepthBelowSeaMeters { get; init; }

    public bool MinDepthWithinTolerance { get; init; }

    public bool AllParametersWithinTolerance { get; init; }
}
