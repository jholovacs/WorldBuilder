using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Contracts;

/// <summary>Client supplies full <see cref="ErosionParams"/> (typically from <c>GET /api/terrain/defaults</c> with field overrides).</summary>
public sealed class TerrainPreviewRequest
{
    public uint Seed { get; init; }

    public required ErosionParams ErosionParams { get; init; }
}
