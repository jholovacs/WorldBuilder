using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using WorldBuilder.Domain.Simulation;
using WorldBuilder.Domain.Terrain;
using WorldBuilder.Domain.Worlds;
using WorldBuilder.Api.Hubs;
using WorldBuilder.Api.SignalR;
using WorldBuilder.Api.Storage;

namespace WorldBuilder.Api.Terrain;

public sealed class TerrainGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IWorldDefinitionStore _store;
    private readonly IHubContext<WorldProgressHub> _hubContext;

    public TerrainGenerationService(IWorldDefinitionStore store, IHubContext<WorldProgressHub> hubContext)
    {
        _store = store;
        _hubContext = hubContext;
    }

    public async Task<TerrainManifestDocument> EnsureOceanBaselineAsync(Guid worldId, CancellationToken cancellationToken = default)
    {
        var world = await _store.GetAsync(worldId, cancellationToken)
                    ?? throw new InvalidOperationException("World not found.");

        var terrainDir = Path.Combine(_store.WorldDirectory(worldId), "terrain");
        Directory.CreateDirectory(terrainDir);

        var manifestPath = Path.Combine(terrainDir, "manifest.json");
        var manifest = File.Exists(manifestPath)
            ? await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false) ?? new TerrainManifestDocument()
            : new TerrainManifestDocument();

        var floor = -(float)(world.MinGroundDepthBelowSeaLevelMeters * 0.25);
        manifest.ManifestVersion = 1;
        manifest.Phase = "oceanBaseline";
        manifest.Baseline = new TerrainBaselineSection
        {
            SeaLevelMeters = 0,
            UniformOceanFloorElevationMeters = floor,
            FeaturelessOcean = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        await SaveManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public async Task<TerrainGenerationJobResult> GenerateTerrainAsync(Guid worldId, int chunkCount, CancellationToken cancellationToken = default)
    {
        if (chunkCount < TerrainGenerationLimits.MinChunkCount || chunkCount > TerrainGenerationLimits.MaxChunkCount)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));

        var world = await _store.GetAsync(worldId, cancellationToken)
                    ?? throw new InvalidOperationException("World not found.");

        try
        {
            await EmitTerrainProgressAsync(
                    worldId,
                    world.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "preparing",
                        Message = "Computing elevation grid…",
                        Progress01 = 0.03,
                        TotalChunks = chunkCount,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureOceanBaselineAsync(worldId, cancellationToken).ConfigureAwait(false);

            await EmitTerrainProgressAsync(
                    worldId,
                    world.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "clearingChunks",
                        Message = "Clearing prior terrain chunk files…",
                        Progress01 = 0.06,
                        TotalChunks = chunkCount,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var worldDir = _store.WorldDirectory(worldId);
            var terrainDir = Path.Combine(worldDir, "terrain");
            var chunksDir = Path.Combine(terrainDir, "chunks");
            Directory.CreateDirectory(chunksDir);

            foreach (var existing in Directory.EnumerateFiles(chunksDir, "chunk-*.bin"))
                File.Delete(existing);

            var (gw, gh, samplesPerChunk) = ComputeGridDimensions(chunkCount, world.EstimatedSurfaceCellCount);

            await EmitTerrainProgressAsync(
                    worldId,
                    world.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "uplifting",
                        Message = $"Building heights ({gw}×{gh} samples; iterative uplift until water coverage matches target)…",
                        Progress01 = 0.08,
                        TargetWaterFraction = world.SurfaceWaterCoveragePercent / 100.0,
                        TotalChunks = chunkCount,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            void BridgeProgress(TerrainElevationProgress p)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var phase = p.Completed ? "upliftingComplete" : "uplifting";
                var msg = new TerrainProgressMessage
                {
                    JobKind = "terrainGeneration",
                    Phase = phase,
                    Iteration = p.Iteration,
                    ActualWaterFraction = p.ActualWaterFraction,
                    TargetWaterFraction = p.TargetWaterFraction,
                    TotalChunks = chunkCount,
                    Message = p.Completed
                        ? $"Uplift finished after {p.Iteration} iteration(s)."
                        : $"Iteration {p.Iteration}: water {(p.ActualWaterFraction * 100):F2}% (target {(p.TargetWaterFraction * 100):F2}%).",
                    Progress01 = p.Completed ? 0.78 : 0.08 + Math.Clamp((double)p.Iteration / 500_000.0, 0, 1) * 0.68,
                };

                EmitTerrainProgressFireForget(worldId, world.Name, msg);
            }

            var (heights, compliance) = TerrainElevationGenerator.Generate(world, gw, gh, BridgeProgress, cancellationToken);

            FactorChunks(chunkCount, out var cols, out var rows);

            for (var c = 0; c < chunkCount; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ChunkBounds(c, cols, rows, gw, gh, out var x0, out var x1, out var y0, out var y1);
                var cw = Math.Max(1, x1 - x0);
                var ch = Math.Max(1, y1 - y0);
                var buf = new float[cw * ch];
                var k = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                        buf[k++] = heights[y * gw + x];
                }

                var chunkPath = Path.Combine(chunksDir, $"chunk-{c:D4}.bin");
                await TerrainChunkCodec.WriteChunkAsync(chunkPath, c, cw, ch, buf, cancellationToken).ConfigureAwait(false);

                var frac = (c + 1) / (double)chunkCount;
                await EmitTerrainProgressAsync(
                        worldId,
                        world.Name,
                        new TerrainProgressMessage
                        {
                            Phase = "writingChunks",
                            Message = $"Writing chunk {c + 1} of {chunkCount}…",
                            Progress01 = 0.78 + frac * 0.18,
                            CurrentChunk = c + 1,
                            TotalChunks = chunkCount,
                            ActualWaterFraction = compliance.ActualWaterFraction,
                            TargetWaterFraction = compliance.TargetWaterFraction,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var manifestPath = Path.Combine(terrainDir, "manifest.json");
            var manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false) ?? new TerrainManifestDocument();
            manifest.ManifestVersion = 1;
            manifest.Phase = "terrainGenerated";
            manifest.Generation = new TerrainGenerationSection
            {
                ChunkCount = chunkCount,
                GridWidth = gw,
                GridHeight = gh,
                SamplesPerChunk = samplesPerChunk,
                CryptoSampleScheme = "sha256-iterative-uplift-v1",
                GeneratedUtc = DateTimeOffset.UtcNow,
                Compliance = compliance,
            };

            await EmitTerrainProgressAsync(
                    worldId,
                    world.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "savingManifest",
                        Message = "Saving terrain manifest…",
                        Progress01 = 0.97,
                        TotalChunks = chunkCount,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await SaveManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

            world.LastCompletedPhase = SimulationPhase.TerrainElevationGenerated.ToString();
            world.ModifiedUtc = DateTimeOffset.UtcNow;
            await _store.SaveAsync(world, cancellationToken).ConfigureAwait(false);

            await EmitTerrainProgressAsync(
                    worldId,
                    world.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "complete",
                        Message = compliance.AllParametersWithinTolerance
                            ? "Terrain generation finished — all compliance checks within tolerance."
                            : $"Terrain generation finished — water {(compliance.ActualWaterFraction * 100):F2}% (some metrics outside ±2% tolerance).",
                        Progress01 = 1,
                        ActualWaterFraction = compliance.ActualWaterFraction,
                        TargetWaterFraction = compliance.TargetWaterFraction,
                        TotalChunks = chunkCount,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new TerrainGenerationJobResult(manifest, compliance);
        }
        catch (OperationCanceledException)
        {
            await EmitTerrainProgressAsync(
                    worldId,
                    world?.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "cancelled",
                        Message = "Terrain generation was cancelled.",
                        Progress01 = null,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
        catch (Exception ex)
        {
            await EmitTerrainProgressAsync(
                    worldId,
                    world?.Name,
                    new TerrainProgressMessage
                    {
                        Phase = "failed",
                        Message = ex.Message,
                        Progress01 = null,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    private Task EmitTerrainProgressAsync(Guid worldId, string? worldName, TerrainProgressMessage msg, CancellationToken cancellationToken)
    {
        msg.JobKind = "terrainGeneration";
        msg.WorldId = worldId;
        msg.WorldName = worldName;
        msg.TimestampUtc = DateTimeOffset.UtcNow;
        return _hubContext.Clients.Group(WorldProgressHub.GroupName(worldId)).SendAsync("TerrainProgress", msg, cancellationToken);
    }

    private void EmitTerrainProgressFireForget(Guid worldId, string? worldName, TerrainProgressMessage msg)
    {
        msg.JobKind = "terrainGeneration";
        msg.WorldId = worldId;
        msg.WorldName = worldName;
        msg.TimestampUtc = DateTimeOffset.UtcNow;
        _ = _hubContext.Clients.Group(WorldProgressHub.GroupName(worldId)).SendAsync("TerrainProgress", msg, CancellationToken.None);
    }
    public async Task<TerrainManifestDocument?> GetManifestAsync(Guid worldId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_store.WorldDirectory(worldId), "terrain", "manifest.json");
        if (!File.Exists(path))
            return null;

        return await LoadManifestAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetChunkFilePath(Guid worldId, int chunkIndex, [NotNullWhen(true)] out string? path)
    {
        path = Path.Combine(_store.WorldDirectory(worldId), "terrain", "chunks", $"chunk-{chunkIndex:D4}.bin");
        return File.Exists(path);
    }

    private static async Task<TerrainManifestDocument?> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TerrainManifestDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveManifestAsync(string path, TerrainManifestDocument manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    internal static (int gw, int gh, int samplesPerChunk) ComputeGridDimensions(int chunkCount, long estimatedCells)
    {
        long targetTotal = Math.Clamp((long)chunkCount * 4096L, chunkCount * 256L, Math.Min(estimatedCells, 800_000L));
        var samplesPerChunk = (int)Math.Max(128L, targetTotal / chunkCount);
        var total = samplesPerChunk * chunkCount;
        var gw = (int)Math.Ceiling(Math.Sqrt(total));
        var gh = (int)Math.Ceiling(total / (double)gw);
        while (gw * gh < total)
            gh++;

        return (gw, gh, samplesPerChunk);
    }

    internal static void FactorChunks(int chunkCount, out int cols, out int rows)
    {
        cols = (int)Math.Ceiling(Math.Sqrt(chunkCount));
        rows = (int)Math.Ceiling(chunkCount / (double)cols);
    }

    internal static void ChunkBounds(int chunkIndex, int cols, int rows, int gw, int gh, out int x0, out int x1, out int y0, out int y1)
    {
        var cx = chunkIndex % cols;
        var cy = chunkIndex / cols;
        x0 = cx * gw / cols;
        x1 = (cx + 1) * gw / cols;
        if (cx == cols - 1)
            x1 = gw;

        y0 = cy * gh / rows;
        y1 = (cy + 1) * gh / rows;
        if (cy == rows - 1)
            y1 = gh;
    }
}

public sealed record TerrainGenerationJobResult(TerrainManifestDocument Manifest, TerrainComplianceReport Compliance);
