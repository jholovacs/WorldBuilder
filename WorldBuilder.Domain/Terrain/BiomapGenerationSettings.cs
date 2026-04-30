namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Unified biome / engine splat mask (RGBA): snow, vegetation proximity, cliffs, sedimentary ground.
/// Omitted JSON → server applies <see cref="Curated"/>.
/// </summary>
public readonly record struct BiomapGenerationSettings
{
    /// <summary>When false, biome PNG is skipped.</summary>
    public bool Enabled { get; init; }

    /// <summary>Elevation threshold for snow mask (meters).</summary>
    public float SnowLineMeters { get; init; }

    /// <summary>Meters ± around the snow line for soft blending (before jitter).</summary>
    public float SnowSoftBandMeters { get; init; }

    /// <summary>Random jitter on the snow transition (meters, deterministic per cell).</summary>
    public float SnowJitterMeters { get; init; }

    /// <summary>
    /// Minimum horizontal reach for moisture halo from standing water mask (ocean + lakes).
    /// </summary>
    public float MoistureReachMinMeters { get; init; }

    /// <summary>Maximum reach clamp for blur radius construction.</summary>
    public float MoistureReachMaxMeters { get; init; }

    /// <summary>Gaussian-like blur spread (meters) applied to occupancy; typically between min/max moisture reach.</summary>
    public float MoistureBlurSigmaMeters { get; init; }

    /// <summary>Flux map contribution to vegetation [0,1]-ish weight alongside moisture.</summary>
    public float VegetationFlowWeight { get; init; }

    /// <summary>Blur moisture contribution vs flow.</summary>
    public float VegetationMoistureWeight { get; init; }

    /// <summary>Hydraulic deposition blend into vegetation corridors (helps riparian look).</summary>
    public float VegetationDepositWeight { get; init; }

    /// <summary>Slope cutoff for rock/cliff mask (degrees).</summary>
    public float CliffSlopeDegrees { get; init; }

    /// <summary>Degrees of soft transition below/above cliff threshold.</summary>
    public float CliffSlopeSoftDegrees { get; init; }

    /// <summary>Normalized deposit intensity above which dirt alpha is boosted (0–1).</summary>
    public float DirtSedimentIntensity01 { get; init; }

    /// <summary>Weight of high deposition vs leftover complement in dirt channel.</summary>
    public float DirtSedimentVsComplementWeight { get; init; }

    internal static BiomapGenerationSettings Curated => new()
    {
        Enabled = true,
        SnowLineMeters = 1800f,
        SnowSoftBandMeters = 120f,
        SnowJitterMeters = 42f,
        MoistureReachMinMeters = 50f,
        MoistureReachMaxMeters = 100f,
        MoistureBlurSigmaMeters = 72f,
        VegetationFlowWeight = 0.55f,
        VegetationMoistureWeight = 0.82f,
        VegetationDepositWeight = 0.28f,
        CliffSlopeDegrees = 35f,
        CliffSlopeSoftDegrees = 4f,
        DirtSedimentIntensity01 = 0.38f,
        DirtSedimentVsComplementWeight = 0.65f,
    };

    internal static BiomapGenerationSettings Disabled => Curated with { Enabled = false };
}
