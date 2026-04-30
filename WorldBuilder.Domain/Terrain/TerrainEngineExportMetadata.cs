namespace WorldBuilder.Domain.Terrain;

/// <summary>Companion JSON for Unity/Unreal import — aligns with quantized heightmap RAW/PNG and splat semantics.</summary>
public sealed class TerrainEngineExportMetadata
{
    /// <summary>Horizontal extent across the sampled grid (matches default 10 km tile).</summary>
    public double WorldScale { get; init; }

    /// <summary>Maximum physical terrain height tuning from erosion (caps vertical scale).</summary>
    public double MaxElevation { get; init; }

    /// <summary>Sea level in meters for water/lake masking.</summary>
    public double SeaLevel { get; init; }

    public int HeightmapWidth { get; init; }

    public int HeightmapHeight { get; init; }

    /// <summary>Horizontal spacing between height samples after export (<c>WorldScale / (width - 1)</c>).</summary>
    public double CellSizeMeters { get; init; }

    /// <summary>Values used when encoding 16-bit height (restore: <c>min + (u16/65535)*range</c>).</summary>
    public double HeightQuantizationMinMeters { get; init; }

    public double HeightQuantizationMaxMeters { get; init; }

    public string HeightmapNotes { get; init; } =
        "16-bit PNG and RAW use linear mapping from quantization min/max to 0–65535 (little-endian RAW, row-major, Y ascending).";

    public TerrainSplatChannelMetadata SplatRgbaChannels { get; init; } = new(
        RSlopeRockCliffs: "Gradient magnitude normalized (steep terrain).",
        GFlowMoistureGrass: "Hydraulic cumulative flow exposure (normalized, upsampled).",
        BDepressionLakes: "Combination of underwater (sea-level) mask and depression-filled lake depths.",
        AFlatlandsSand: "Gentle slopes excluding strong water/depression cues.");
}

/// <inheritdoc cref="TerrainEngineExportMetadata.SplatRgbaChannels"/>
public sealed record TerrainSplatChannelMetadata(
    string RSlopeRockCliffs,
    string GFlowMoistureGrass,
    string BDepressionLakes,
    string AFlatlandsSand);
