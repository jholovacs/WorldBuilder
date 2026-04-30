using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Storage;

public sealed class FileWorldDefinitionStore : IWorldDefinitionStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<WorldStorageOptions> _worldOptions;
    private readonly IWebHostEnvironment _env;

    public FileWorldDefinitionStore(
        IOptions<WorldStorageOptions> options,
        IWebHostEnvironment env,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _worldOptions = options;
        _env = env;
    }

    private string ResolveRoot() =>
        StoragePathResolver.ResolveWorldFilesRoot(_httpContextAccessor.HttpContext, _worldOptions.Value, _env.ContentRootPath);

    public string WorldDirectory(Guid id) => Path.Combine(ResolveRoot(), id.ToString("N"));

    public async Task<IReadOnlyList<WorldSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
            return Array.Empty<WorldSummary>();

        var results = new List<WorldSummary>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, "definition.json");
            if (!File.Exists(path))
                continue;

            await using var stream = File.OpenRead(path);
            var def = await JsonSerializer.DeserializeAsync<WorldDefinition>(stream, _jsonOptions, cancellationToken);
            if (def is null)
                continue;

            results.Add(ToSummary(def));
        }

        return results.OrderByDescending(w => w.CreatedUtc).ToList();
    }

    public async Task<WorldDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = DefinitionPath(id);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorldDefinition>(stream, _jsonOptions, cancellationToken);
    }

    public async Task SaveAsync(WorldDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var root = Path.GetFullPath(ResolveRoot());
        var folder = Path.Combine(root, definition.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "definition.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, definition, _jsonOptions, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var folder = WorldFolder(id);
        if (!Directory.Exists(folder))
            return Task.FromResult(false);

        Directory.Delete(folder, recursive: true);
        return Task.FromResult(true);
    }

    private static WorldSummary ToSummary(WorldDefinition def) =>
        new(def.Id, def.Name, def.Shape, def.CreatedUtc);

    private string WorldFolder(Guid id) => Path.Combine(ResolveRoot(), id.ToString("N"));

    private string DefinitionPath(Guid id) => Path.Combine(WorldFolder(id), "definition.json");
}

public sealed class WorldStorageOptions
{
    public const string SectionName = "WorldStorage";

    /// <summary>Root folder for world metadata and chunked artifacts (relative to content root unless absolute).</summary>
    public string RootPath { get; set; } = "App_Data/worlds";
}
