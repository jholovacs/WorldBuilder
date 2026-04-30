namespace WorldBuilder.Domain.WorldShapes;

/// <summary>
/// Topology class for generated worlds. Drives coordinate system selection and physics constraints.
/// </summary>
public enum WorldShape
{
    /// <summary>Closed surface; polar/azimuth + elevation relative to radial direction.</summary>
    Sphere,

    /// <summary>Torus / doughnut; toroidal coordinates (major/minor angles + clearance from nominal shell).</summary>
    Torus,

    /// <summary>Open terrain patch or bounded continent in Euclidean space (X, Y, Z).</summary>
    Continent,
}
