namespace WorldBuilder.Api.Terrain;



/// <summary>

/// Resolved procedural terrain scaling used by <see cref="TerrainGenerator"/> defaults — matches heightmap preview generation.

/// Includes terrain-type tuning mirrored from <see cref="WorldBuilder.Domain.Terrain.ErosionParams"/> for Angular clients.

/// </summary>

public sealed record TerrainMetadataResponse(

    float CellSizeMeters,

    float MaxElevationMeters,

    float SeaLevelMeters,

    float HeightPower,

    float TerrainTypeScale,

    float PlainsPersistence,

    float MountainThreshold,

    float SmoothingStrength,

    float SmoothingRadius,

    float HydraulicDepositDistributionSigmaPx);

