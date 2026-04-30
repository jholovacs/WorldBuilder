using WorldBuilder.Domain.WorldShapes;

namespace WorldBuilder.Domain.Worlds;

public sealed record WorldSummary(Guid Id, string Name, WorldShape Shape, DateTimeOffset CreatedUtc);
