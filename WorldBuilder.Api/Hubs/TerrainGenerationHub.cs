using Microsoft.AspNetCore.SignalR;
using WorldBuilder.Api.Contracts;
using WorldBuilder.Api.Terrain;

namespace WorldBuilder.Api.Hubs;

/// <summary>Streams procedural terrain preview generation progress for the dashboard.</summary>
public sealed class TerrainGenerationHub(TerrainGenerationCoordinator coordinator) : Hub
{
    /// <summary>Blocking CPU-heavy generation runs on the thread pool inside the coordinator.</summary>
    public Task GenerateTerrain(TerrainPreviewRequest request) =>
        coordinator.RunAsync(Context.ConnectionId, request, Context.ConnectionAborted);
}
