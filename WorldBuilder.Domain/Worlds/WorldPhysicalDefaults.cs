using WorldBuilder.Domain.WorldShapes;

namespace WorldBuilder.Domain.Worlds;

/// <summary>
/// Physical defaults tied to total surface area. Elevations scale linearly with √(A/A<sub>Earth</sub>)
/// against representative Earth terrain extremes and median land height.
/// </summary>
public static class WorldPhysicalDefaults
{
    /// <summary>Approximate Earth's total surface area including oceans (km²).</summary>
    public const double EarthTotalSurfaceAreaKm2 = 510_082_293d;

    /// <summary>Default new-world surface area including water (km²).</summary>
    public const double DefaultSurfaceAreaKm2 = 100d;

    /// <summary>Default fraction of surface modeled as water (e.g. oceans).</summary>
    public const double DefaultSurfaceWaterCoveragePercent = 70d;

    /// <summary>Representative maximum dry/near-ground elevation above sea level (Everest-scale).</summary>
    public const double EarthReferenceMaxElevationAboveSeaMeters = 8848d;

    /// <summary>
    /// Representative minimum ground elevation expressed as depth below sea level (positive meters),
    /// Challenger Deep-scale trench floor.
    /// </summary>
    public const double EarthReferenceMinGroundDepthBelowSeaMeters = 10_935d;

    /// <summary>Approximate median land elevation above sea level (global dry land).</summary>
    public const double EarthReferenceMedianLandElevationAboveSeaMeters = 840d;

    /// <summary>Ground sampling granularity (m² per cell); ~1 m² per stored terrain sample.</summary>
    public const double DefaultGroundResolutionSquareMeters = 1d;

    /// <summary>√(A/A<sub>Earth</sub>) — scales characteristic lengths vs Earth when matching surface areas.</summary>
    public static double LinearScaleFactorFromSurfaceAreaKm2(double surfaceAreaKm2) =>
        surfaceAreaKm2 > 0 ? Math.Sqrt(surfaceAreaKm2 / EarthTotalSurfaceAreaKm2) : 0;

    /// <summary>Earth-relational elevation defaults from surface area.</summary>
    public static SuggestedTerrainElevations SuggestedElevationsFromSurfaceAreaKm2(double surfaceAreaKm2)
    {
        var u = LinearScaleFactorFromSurfaceAreaKm2(surfaceAreaKm2);
        return new SuggestedTerrainElevations(
            MaxGroundElevationAboveSeaLevelMeters: EarthReferenceMaxElevationAboveSeaMeters * u,
            MinGroundDepthBelowSeaLevelMeters: EarthReferenceMinGroundDepthBelowSeaMeters * u,
            MedianGroundElevationAboveSeaLevelMeters: EarthReferenceMedianLandElevationAboveSeaMeters * u,
            LinearScaleVersusEarth: u);
    }

    /// <summary>Sphere radius (meters) from total surface area A = 4πR².</summary>
    public static double SphereRadiusMetersFromSurfaceAreaKm2(double surfaceAreaKm2)
    {
        var areaM2 = surfaceAreaKm2 * 1_000_000d;
        return Math.Sqrt(areaM2 / (4 * Math.PI));
    }

    /// <summary>Square continent patch half-extent (meters): side² = A ⇒ half = √A / 2.</summary>
    public static double ContinentHalfExtentMetersFromSurfaceAreaKm2(double surfaceAreaKm2)
    {
        var areaM2 = surfaceAreaKm2 * 1_000_000d;
        return Math.Sqrt(areaM2) / 2d;
    }

    /// <summary>
    /// Torus radii from total surface area A = 4π² R r with R chosen at the sphere-equivalent radius √(A/(4π)).
    /// </summary>
    public static void TorusRadiiMetersFromSurfaceAreaKm2(double surfaceAreaKm2, out double majorRadiusMeters, out double minorRadiusMeters)
    {
        var areaM2 = surfaceAreaKm2 * 1_000_000d;
        majorRadiusMeters = Math.Sqrt(areaM2 / (4 * Math.PI));
        minorRadiusMeters = areaM2 / (4 * Math.PI * Math.PI * majorRadiusMeters);
    }

    /// <summary>Number of ground samples covering the surface at the given resolution (≥ 1).</summary>
    public static long EstimatedSurfaceCellCount(double surfaceAreaKm2, double groundResolutionSquareMeters)
    {
        if (surfaceAreaKm2 <= 0 || groundResolutionSquareMeters <= 0)
            return 0;
        var areaM2 = surfaceAreaKm2 * 1_000_000d;
        return (long)Math.Max(1, Math.Round(areaM2 / groundResolutionSquareMeters));
    }

    /// <summary>Fills geometry fields derived from surface area for each world shape.</summary>
    public static void ApplyShapeGeometry(
        WorldShape shape,
        double surfaceAreaKm2,
        out double referenceRadiusMeters,
        out double? torusMajorRadiusMeters,
        out double? torusMinorRadiusMeters,
        out double? patchExtentXMeters,
        out double? patchExtentZMeters)
    {
        torusMajorRadiusMeters = null;
        torusMinorRadiusMeters = null;
        patchExtentXMeters = null;
        patchExtentZMeters = null;

        switch (shape)
        {
            case WorldShape.Sphere:
                referenceRadiusMeters = SphereRadiusMetersFromSurfaceAreaKm2(surfaceAreaKm2);
                break;
            case WorldShape.Torus:
                TorusRadiiMetersFromSurfaceAreaKm2(surfaceAreaKm2, out var maj, out var min);
                referenceRadiusMeters = maj;
                torusMajorRadiusMeters = maj;
                torusMinorRadiusMeters = min;
                break;
            case WorldShape.Continent:
                referenceRadiusMeters = SphereRadiusMetersFromSurfaceAreaKm2(surfaceAreaKm2);
                var half = ContinentHalfExtentMetersFromSurfaceAreaKm2(surfaceAreaKm2);
                patchExtentXMeters = half;
                patchExtentZMeters = half;
                break;
            default:
                referenceRadiusMeters = SphereRadiusMetersFromSurfaceAreaKm2(surfaceAreaKm2);
                break;
        }
    }
}

/// <summary>Elevation envelope derived from Earth comparisons at a given surface area.</summary>
public readonly record struct SuggestedTerrainElevations(
    double MaxGroundElevationAboveSeaLevelMeters,
    double MinGroundDepthBelowSeaLevelMeters,
    double MedianGroundElevationAboveSeaLevelMeters,
    double LinearScaleVersusEarth);
