using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Contracts;

/// <summary>
/// Replay thermal + hydraulic erosion on an existing 16-bit grayscale heightmap PNG (same encoding as preview output).
/// </summary>
public sealed class TerrainRefineRequest
{
    /// <summary>Base64-encoded PNG bytes from <c>/api/terrain/preview</c>, SignalR download, or a prior refine response.</summary>
    public required string HeightmapPngBase64 { get; init; }

    /// <summary>Tuning for this refinement pass — thermal iterations, hydraulic passes, strengths, etc.</summary>
    public required ErosionParams ErosionParams { get; init; }

    /// <summary>Random seed for hydraulic droplet placement (distinct values explore alternate erosion paths).</summary>
    public uint HydraulicSeed { get; init; }
}
