using WorldBuilder.Domain.WorldShapes;
using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Contracts;

public sealed class CreateWorldRequest
{
    public string Name { get; set; } = string.Empty;

    public WorldShape Shape { get; set; }

    public ulong? Seed { get; set; }

    /// <summary>Total modeled surface area including water (km²).</summary>
    public double TotalSurfaceAreaSquareKilometers { get; set; } = WorldPhysicalDefaults.DefaultSurfaceAreaKm2;

    /// <summary>Percent of surface modeled as water (0–100).</summary>
    public double SurfaceWaterCoveragePercent { get; set; } = WorldPhysicalDefaults.DefaultSurfaceWaterCoveragePercent;

    /// <summary>Ground sample footprint (m² per cell).</summary>
    public double GroundResolutionSquareMeters { get; set; } = WorldPhysicalDefaults.DefaultGroundResolutionSquareMeters;

    /// <summary>
    /// Maximum elevation above sea level (meters); omit to derive from Earth-relative scaling at this surface area.
    /// </summary>
    public double? MaxGroundElevationAboveSeaLevelMeters { get; set; }

    /// <summary>Positive depth below sea level for the deepest modeled ground (meters).</summary>
    public double? MinGroundDepthBelowSeaLevelMeters { get; set; }

    /// <summary>Desired median terrain height above sea level (meters).</summary>
    public double? MedianGroundElevationAboveSeaLevelMeters { get; set; }
}
