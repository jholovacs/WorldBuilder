namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Persistable procedural terrain identity: deterministic regeneration uses <see cref="Seed"/> and merged <see cref="ErosionParams"/>.
/// </summary>
public sealed record TerrainMap(string MapName, uint Seed, ErosionParams ErosionParams);
