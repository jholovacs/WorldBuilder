using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Contracts;

public sealed class SaveTerrainMapRequest
{
    public required string MapName { get; init; }

    public uint Seed { get; init; }

    /// <summary>Optional; omitted fields merge via API defaults matching procedural preview generation.</summary>
    public ErosionParams? ErosionParams { get; init; }

    /// <summary>
    /// Optional 16-bit grayscale PNG heightmap (base64). When set, skips full procedural regeneration and persists this image
    /// (must match <see cref="TerrainGenerator.DefaultResolution"/>² — same format as preview/download).
    /// </summary>
    public string? HeightmapPngBase64 { get; init; }
}

public sealed record TerrainMapSavedResponse(string MapName, DateTimeOffset SavedUtc);
