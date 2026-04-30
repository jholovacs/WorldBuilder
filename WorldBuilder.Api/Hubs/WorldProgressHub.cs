using Microsoft.AspNetCore.SignalR;

namespace WorldBuilder.Api.Hubs;

/// <summary>Clients subscribe per-world groups to receive terrain and simulation progress.</summary>
public sealed class WorldProgressHub : Hub
{
    public async Task SubscribeWorld(string worldId)
    {
        if (!Guid.TryParse(worldId, out var gid))
            return;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(gid));
    }

    public async Task UnsubscribeWorld(string worldId)
    {
        if (!Guid.TryParse(worldId, out var gid))
            return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(gid));
    }

    internal static string GroupName(Guid worldId) => $"world-{worldId:N}";
}
