using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Storage;

public sealed class TerrainMapsOptions
{
    public const string SectionName = "TerrainMaps";

    /// <summary>
    /// Optional legacy folder also scanned when listing loading old saves (relative to API content root unless absolute).
    /// New saves write to <see cref="StoragePathResolver.ResolveWorldFilesRoot"/> for the active request (<c>X-World-Storage-Root</c>), same directory as worlds.
    /// </summary>
    public string? LegacyTerrainMapsPath { get; set; } = "App_Data/terrain-maps";
}

/// <summary>Persisted procedural terrain previews (<see cref="TerrainMap"/> + PNG).</summary>
public interface ITerrainMapStore
{
    Task<IReadOnlyList<TerrainMapListEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<(DateTimeOffset SavedUtc, string CanonicalMapName)> SaveAsync(
        TerrainMap map,
        ReadOnlyMemory<byte> heightmapPng,
        int resolution,
        CancellationToken cancellationToken = default);

    bool TryGetHeightmapFullPath(string mapName, [NotNullWhen(true)] out string? pngFullPath);

    bool TryGetTerrainJsonFullPath(string mapName, [NotNullWhen(true)] out string? jsonFullPath);
}

/// <summary>Summary row used by GET list.</summary>
public sealed record TerrainMapListEntry(string MapName, uint Seed, DateTimeOffset SavedUtc, int Resolution);

/// <summary>Writable terrain manifest alongside PNG.</summary>
internal sealed class TerrainMapPersistedDocument
{
    public required string MapName { get; init; }

    public required uint Seed { get; init; }

    public required ErosionParams ErosionParams { get; init; }

    public DateTimeOffset SavedUtc { get; init; }

    public int Resolution { get; init; }
}

/// <summary>
/// Stores each map as <c>{slug}.terrain.json</c> + <c>{slug}.heightmap.png</c> directly under the world-files folder
/// (same root as world definitions via <see cref="WorldStorageHeaders.ClientRootBase64"/>).
/// </summary>
public sealed class FileTerrainMapStore : ITerrainMapStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<WorldStorageOptions> _worldOptions;
    private readonly IWebHostEnvironment _env;
    private readonly string? _legacyRoot;

    public FileTerrainMapStore(
        IHttpContextAccessor httpContextAccessor,
        IOptions<WorldStorageOptions> worldOptions,
        IWebHostEnvironment env,
        IOptions<TerrainMapsOptions> terrainMapsOptions)
    {
        _httpContextAccessor = httpContextAccessor;
        _worldOptions = worldOptions;
        _env = env;

        _legacyRoot = null;
        var legacyConfigured = terrainMapsOptions.Value.LegacyTerrainMapsPath;
        if (!string.IsNullOrWhiteSpace(legacyConfigured))
        {
            var legCombined = Path.IsPathRooted(legacyConfigured)
                ? legacyConfigured
                : Path.Combine(env.ContentRootPath, legacyConfigured);
            _legacyRoot = Path.GetFullPath(legCombined);
        }
    }

    private string TerrainWriteFolder =>
        StoragePathResolver.ResolveWorldFilesRoot(
            _httpContextAccessor.HttpContext,
            _worldOptions.Value,
            _env.ContentRootPath);

    /// <summary>Whitespace runs become a single underscore; leading/trailing space is trimmed first.</summary>
    private static string FoldWhitespaceRunsToUnderscores(ReadOnlySpan<char> trimmed)
    {
        var sb = new StringBuilder(trimmed.Length);
        bool needUnderscore = false;

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                needUnderscore = true;
                continue;
            }

            if (needUnderscore && sb.Length > 0)
                sb.Append('_');
            needUnderscore = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool TryNormalizeSlug(string mapName, out string slug)
    {
        slug = "";
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        var trimmed = mapName.Trim();
        if (trimmed.Length > 64)
            return false;

        string folded = FoldWhitespaceRunsToUnderscores(trimmed);
        if (folded.Length == 0 || folded.Length > 64)
            return false;

        foreach (char c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                continue;
            return false;
        }

        slug = folded.ToLowerInvariant();
        return true;
    }

    private static string TerrainJsonPath(string root, string slug) =>
        Path.Combine(root, $"{slug}.terrain.json");

    private static string HeightmapPath(string root, string slug) =>
        Path.Combine(root, $"{slug}.heightmap.png");

    /// <inheritdoc />
    public async Task<IReadOnlyList<TerrainMapListEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var primary = TerrainWriteFolder;
        var bestBySlug = new Dictionary<string, TerrainMapListEntry>(StringComparer.OrdinalIgnoreCase);

        await MergeListFromDirectoryAsync(primary, bestBySlug, cancellationToken).ConfigureAwait(false);
        if (_legacyRoot != null && !string.Equals(_legacyRoot, primary, StringComparison.OrdinalIgnoreCase))
            await MergeListFromDirectoryAsync(_legacyRoot, bestBySlug, cancellationToken).ConfigureAwait(false);

        return bestBySlug.Values.OrderByDescending(e => e.SavedUtc).ToList();
    }

    private async Task MergeListFromDirectoryAsync(
        string rootDir,
        Dictionary<string, TerrainMapListEntry> bestBySlug,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDir))
            return;

        foreach (var path in Directory.EnumerateFiles(rootDir, "*.terrain.json"))
        {
            await using var stream = File.OpenRead(path);
            var doc = await JsonSerializer.DeserializeAsync<TerrainMapPersistedDocument>(stream, _jsonOptions, cancellationToken);
            if (doc is null)
                continue;

            string key = doc.MapName.Trim().ToLowerInvariant();
            var entry = new TerrainMapListEntry(doc.MapName, doc.Seed, doc.SavedUtc, doc.Resolution);

            if (!bestBySlug.TryGetValue(key, out var prev) || entry.SavedUtc > prev.SavedUtc)
                bestBySlug[key] = entry;
        }
    }

    /// <inheritdoc />
    public async Task<(DateTimeOffset SavedUtc, string CanonicalMapName)> SaveAsync(
        TerrainMap map,
        ReadOnlyMemory<byte> heightmapPng,
        int resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!TryNormalizeSlug(map.MapName, out var slug))
            throw new ArgumentException(
                "Invalid map name: use ASCII letters, digits, dot, hyphen, underscore; whitespace is folded to underscores (max 64 chars after trim).");

        var folder = TerrainWriteFolder;
        Directory.CreateDirectory(folder);

        var jsonPath = TerrainJsonPath(folder, slug);
        var pngPath = HeightmapPath(folder, slug);

        var utc = DateTimeOffset.UtcNow;
        var doc = new TerrainMapPersistedDocument
        {
            MapName = slug,
            Seed = map.Seed,
            ErosionParams = map.ErosionParams,
            SavedUtc = utc,
            Resolution = resolution,
        };

        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(stream, doc, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(pngPath, heightmapPng.ToArray(), cancellationToken).ConfigureAwait(false);
        return (utc, slug);
    }

    /// <inheritdoc />
    public bool TryGetHeightmapFullPath(string mapName, [NotNullWhen(true)] out string? pngFullPath)
    {
        pngFullPath = null;
        if (!TryNormalizeSlug(mapName, out var slug))
            return false;

        var folder = TerrainWriteFolder;
        var path = HeightmapPath(folder, slug);

        if (!File.Exists(path) && _legacyRoot != null && !string.Equals(_legacyRoot, folder, StringComparison.OrdinalIgnoreCase))
            path = HeightmapPath(_legacyRoot, slug);

        if (!File.Exists(path))
            return false;

        pngFullPath = path;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetTerrainJsonFullPath(string mapName, [NotNullWhen(true)] out string? jsonFullPath)
    {
        jsonFullPath = null;
        if (!TryNormalizeSlug(mapName, out var slug))
            return false;

        var folder = TerrainWriteFolder;
        var path = TerrainJsonPath(folder, slug);

        if (!File.Exists(path) && _legacyRoot != null && !string.Equals(_legacyRoot, folder, StringComparison.OrdinalIgnoreCase))
            path = TerrainJsonPath(_legacyRoot, slug);

        if (!File.Exists(path))
            return false;

        jsonFullPath = path;
        return true;
    }
}
