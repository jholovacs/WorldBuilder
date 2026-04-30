namespace WorldBuilder.Domain.Terrain;

/// <summary>User-configurable chunk count for terrain generation (spatial partition + file count).</summary>
public static class TerrainGenerationLimits
{
    public const int MinChunkCount = 5;

    public const int MaxChunkCount = 100;

    /// <summary>Relative / fractional tolerance vs target scalar parameters (e.g. 0.02 = 2%).</summary>
    public const double ParameterComplianceTolerance = 0.02;

    /// <summary>Absolute tolerance on surface fractions in [0,1] (e.g. 0.02 = ±2 percentage points).</summary>
    public const double SurfaceFractionTolerance = 0.02;
}
