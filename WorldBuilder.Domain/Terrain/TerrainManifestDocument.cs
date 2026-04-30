namespace WorldBuilder.Domain.Terrain;

/// <summary>Serialized to terrain/manifest.json alongside definition.json.</summary>
public sealed class TerrainManifestDocument
{
    public int ManifestVersion { get; set; } = 1;

    /// <summary>none | oceanBaseline | terrainGenerated</summary>
    public string Phase { get; set; } = "none";

    public TerrainBaselineSection? Baseline { get; set; }

    public TerrainGenerationSection? Generation { get; set; }
}

public sealed class TerrainBaselineSection
{
    /// <summary>Sea reference plane (meters).</summary>
    public double SeaLevelMeters { get; set; }

    /// <summary>Uniform elevation everywhere before procedural terrain (meters; typically negative).</summary>
    public double UniformOceanFloorElevationMeters { get; set; }

    public bool FeaturelessOcean { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class TerrainGenerationSection
{
    public int ChunkCount { get; set; }

    public int GridWidth { get; set; }

    public int GridHeight { get; set; }

    public int SamplesPerChunk { get; set; }

    public string CryptoSampleScheme { get; set; } = "sha256-iterative-uplift-v1";

    public DateTimeOffset GeneratedUtc { get; set; }

    public TerrainComplianceReport? Compliance { get; set; }
}
