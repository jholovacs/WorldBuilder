namespace WorldBuilder.Domain.Terrain;

/// <summary>Optional biome preview when running Vulkan shallow-water (GPU kernels + unified API).</summary>
public sealed record ShallowWaterFlowMapOptions
{
    /// <summary>When true, GPU runs <c>CalculateBiomes</c> after the flow simulation and emits an RGBA8 PNG alongside the grayscale flow PNG (channel <c>A</c> = tree-density jitter).</summary>
    public bool IncludeBiomeRgbaPng { get; init; }

    /// <summary>XOR'ed into biome noise seed for reproducible/high-frequency jitter in <c>W</c> tree density.</summary>
    public uint BiomeScatterNoiseSeed { get; init; }

    /// <summary>Elevation threshold for snow mask (metres); passed to biome compute uniforms for live tweaking.</summary>
    public float SnowLineMeters { get; init; } = BiomapGenerationSettings.Curated.SnowLineMeters;

    /// <summary>Sea level / max elevation / biomap softness — merges like full generation via <see cref="TerrainGenerator.ResolveErosionParams"/>.</summary>
    public ErosionParams? ErosionTuning { get; init; }
}
