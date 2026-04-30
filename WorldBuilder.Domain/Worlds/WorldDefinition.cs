using WorldBuilder.Domain.WorldShapes;

namespace WorldBuilder.Domain.Worlds;

/// <summary>
/// Authoritative definition for a generated world (metadata + simulation knobs). Chunk geometry lives separately on disk for scale.
/// </summary>
public sealed class WorldDefinition
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public WorldShape Shape { get; set; }

    /// <summary>Simulation seed for deterministic regeneration.</summary>
    public ulong Seed { get; set; }

    /// <summary>Total modeled surface area including water (km²).</summary>
    public double TotalSurfaceAreaSquareKilometers { get; set; }

    /// <summary>Fraction of surface treated as water cover (0–100).</summary>
    public double SurfaceWaterCoveragePercent { get; set; }

    /// <summary>Highest terrain modeled relative to sea level (meters).</summary>
    public double MaxGroundElevationAboveSeaLevelMeters { get; set; }

    /// <summary>Deepest modeled ground below sea level as a positive depth (meters).</summary>
    public double MinGroundDepthBelowSeaLevelMeters { get; set; }

    /// <summary>Target median terrain height above sea level (meters).</summary>
    public double MedianGroundElevationAboveSeaLevelMeters { get; set; }

    /// <summary>Ground sample footprint (m² per cell); ~1 implies ~1 m² granularity.</summary>
    public double GroundResolutionSquareMeters { get; set; }

    /// <summary>Approximate cell count for full surface coverage at <see cref="GroundResolutionSquareMeters"/>.</summary>
    public long EstimatedSurfaceCellCount { get; set; }

    /// <summary>√(A/A<sub>Earth</sub>) stored at creation for reproducibility.</summary>
    public double LinearScaleVersusEarth { get; set; }

    /// <summary>
    /// Sphere radius (meters), torus characteristic major radius reference, or unused for continent depending on <see cref="Shape"/>.
    /// </summary>
    public double ReferenceRadiusMeters { get; set; }

    /// <summary>For torus: major radius R (hole to tube center).</summary>
    public double? TorusMajorRadiusMeters { get; set; }

    public double? TorusMinorRadiusMeters { get; set; }

    /// <summary>Continent patch half-extents (meters) when <see cref="WorldShape"/> is <see cref="WorldShape.Continent"/>.</summary>
    public double? PatchExtentXMeters { get; set; }

    public double? PatchExtentZMeters { get; set; }

    /// <summary>Highest completed simulation phase for incremental builds.</summary>
    public string LastCompletedPhase { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }
}
