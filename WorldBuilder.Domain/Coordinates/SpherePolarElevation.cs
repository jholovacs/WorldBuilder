namespace WorldBuilder.Domain.Coordinates;

/// <summary>
/// Sphere-oriented sampling: longitude λ (−π..π), latitude φ (−π/2..π/2), elevation h above reference sphere (meters).
/// </summary>
public readonly record struct SpherePolarElevation(double LongitudeRadians, double LatitudeRadians, double ElevationMeters);
