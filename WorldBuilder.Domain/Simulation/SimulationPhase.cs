namespace WorldBuilder.Domain.Simulation;

/// <summary>
/// Ordered phases for procedural terrain aligned with broad Earth-science processes (extensible).
/// </summary>
public enum SimulationPhase
{
    BaseTopology = 0,
    PlateTectonics = 1,
    ThermalIsostasy = 2,
    GravityCollapse = 3,
    Glaciation = 4,
    FluvialErosion = 5,
    AeolianPolish = 6,
    CoastalShelf = 7,

    /// <summary>Synthetic coarse elevation field generated for authoring/export.</summary>
    TerrainElevationGenerated = 8,
}
