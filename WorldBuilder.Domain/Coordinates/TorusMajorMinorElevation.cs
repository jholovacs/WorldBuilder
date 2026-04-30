namespace WorldBuilder.Domain.Coordinates;

/// <summary>
/// Toroidal framing: ψ wraps the major ring (around the hole), θ wraps the tube cross-section,
/// clearance is signed distance from the nominal tube surface along the tube inward normal (meters).
/// </summary>
public readonly record struct TorusMajorMinorElevation(
    double MajorAngleRadians,
    double MinorAngleRadians,
    double ClearanceMeters);
