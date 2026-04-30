namespace WorldBuilder.Api.Contracts;

public sealed class WorldCreationDefaultsResponse
{
    public double SurfaceAreaKm2 { get; init; }

    public double SurfaceWaterCoveragePercent { get; init; }

    public double GroundResolutionSquareMeters { get; init; }

    public double LinearScaleVersusEarth { get; init; }

    public double MaxGroundElevationAboveSeaLevelMeters { get; init; }

    public double MinGroundDepthBelowSeaLevelMeters { get; init; }

    public double MedianGroundElevationAboveSeaLevelMeters { get; init; }

    public long EstimatedSurfaceCellCount { get; init; }

    /// <summary>Sphere-equivalent radius for UI preview (meters).</summary>
    public double SphereEquivalentRadiusMeters { get; init; }
}
