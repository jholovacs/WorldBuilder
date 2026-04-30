namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// High-contrast monochrome scatter mask export (white = plantable trees). Uses hydrology-blurred moisture, slope,
/// snow line gates, plus high-frequency procedural noise multiplier to break tiling.
/// </summary>
public readonly record struct VegetationScatterGenerationSettings
{
    /// <summary>When false, <c>vegetation_scatter.png</c> is not produced.</summary>
    public bool Enabled { get; init; }

    /// <summary>Wet-mask threshold on blurred moisture ∈ [0,1] — must exceed this as a tree-density gate.</summary>
    public float MoistureThreshold01 { get; init; }

    /// <summary>Exclude steep ground (degrees); trees only where slope strictly below.</summary>
    public float MaxSlopeDegrees { get; init; }

    /// <summary>Spatial frequency multiplier on integer cell coords (higher = finer speckling).</summary>
    public float ScatterNoiseScale { get; init; }

    /// <summary>Lower bound on per-cell stochastic density multiplier ∈ [0,1] — increases contrast vs pure white blobs.</summary>
    public float NoiseMultiplierMin01 { get; init; }

    /// <summary>Upper bound on per-cell density multiplier.</summary>
    public float NoiseMultiplierMax01 { get; init; }

    /// <summary>After multiplying gates × noise blend, intensities strictly above this yield white (below → black).</summary>
    public float BinaryCutoff01 { get; init; }

    internal static VegetationScatterGenerationSettings Curated => new()
    {
        Enabled = false,
        MoistureThreshold01 = 0.5f,
        MaxSlopeDegrees = 20f,
        ScatterNoiseScale = 48f,
        NoiseMultiplierMin01 = 0.32f,
        NoiseMultiplierMax01 = 1f,
        BinaryCutoff01 = 0.498f,
    };

    internal static VegetationScatterGenerationSettings Disabled =>
        Curated with { Enabled = false };
}
