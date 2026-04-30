namespace WorldBuilder.Api.Contracts;

/// <summary>Returned when biome preview is bundled with shallow-water GPU output.</summary>
public sealed class TerrainShallowWaterFlowResponse
{
    public required string FlowPngBase64 { get; init; }

    /// <summary>RGBA8 biome PNG (<c>R</c> snow · <c>G</c> veg near water · <c>B</c> cliffs · <c>A</c> dirt).</summary>
    public string? BiomeRgbaPngBase64 { get; init; }
}
