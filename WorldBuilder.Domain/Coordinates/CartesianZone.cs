namespace WorldBuilder.Domain.Coordinates;

/// <summary>
/// Euclidean patch for continent-scale worlds (Unity-friendly XYZ).
/// </summary>
public readonly record struct CartesianZone(double X, double Y, double Z);
