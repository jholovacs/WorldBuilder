using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Storage;

public interface IWorldDefinitionStore
{
    Task<IReadOnlyList<WorldSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<WorldDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(WorldDefinition definition, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Absolute folder containing definition.json for this world.</summary>
    string WorldDirectory(Guid id);
}
