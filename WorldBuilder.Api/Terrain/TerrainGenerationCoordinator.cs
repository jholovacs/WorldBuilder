using Microsoft.AspNetCore.SignalR;
using WorldBuilder.Api.Contracts;
using WorldBuilder.Api.Hubs;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Terrain;

/// <summary>
/// Runs <see cref="TerrainGenerator"/> on a thread-pool thread and streams progress + thumbnails over SignalR.
/// </summary>
public sealed class TerrainGenerationCoordinator(
    IHubContext<TerrainGenerationHub> hubContext,
    TerrainPreviewSessionStore previewSessions,
    TerrainExportService terrainExportService,
    ITerrainGpuCompute gpuCompute)
{
    public Task RunAsync(string connectionId, TerrainPreviewRequest request, CancellationToken ct) =>
        Task.Run(() => RunSync(connectionId, request, ct), ct);

    private void RunSync(string connectionId, TerrainPreviewRequest request, CancellationToken ct)
    {
        var caller = hubContext.Clients.Client(connectionId);

        try
        {
            ArgumentNullException.ThrowIfNull(request);

            var resolved = TerrainGenerator.ResolveErosionParams(request.ErosionParams);
            int w = TerrainGenerator.DefaultResolution;
            int h = TerrainGenerator.DefaultResolution;

            int thermalTotal = Math.Max(1, resolved.ThermalIterations);
            int thermalRelaxTotal = Math.Clamp(resolved.ThermalRelaxIterationsAfterHydraulic, 0, 512);
            int hydraulicTotal = Math.Max(1, resolved.HydraulicPasses);
            int glacierTotal = resolved.Glacier.Enabled ? Math.Max(1, resolved.Glacier.Iterations) : 0;
            int hydrologySteps = resolved.Glacier.Enabled ? 1 : 0;
            int tectonicSteps = resolved.Tectonics.Enabled ? 1 : 0;
            int totalSteps = 1 + tectonicSteps + thermalTotal + hydraulicTotal + thermalRelaxTotal + glacierTotal + hydrologySteps;
            int step = 0;

            bool ThermalThumbEvery(int cur1Based, int total)
            {
                int interval = Math.Max(1, total / 10);
                return cur1Based % interval == 0 || cur1Based == total;
            }

            bool GlacierThumbEvery(int cur1Based, int total)
            {
                int interval = Math.Max(1, total / 8);
                return cur1Based % interval == 0 || cur1Based == total;
            }

            void Push(string phase, int iteration, int iterationTotal, string? previewBase64)
            {
                ct.ThrowIfCancellationRequested();
                step++;
                double p01 = Math.Clamp(step / (double)Math.Max(1, totalSteps), 0d, 1d);
                caller.SendAsync(
                    "TerrainProgress",
                    new TerrainGenerationProgressMessage
                    {
                        Phase = phase,
                        Iteration = iteration,
                        IterationTotal = iterationTotal,
                        Progress01 = p01,
                        PreviewImageBase64 = previewBase64,
                    },
                    ct).GetAwaiter().GetResult();
            }

            var callbacks = new TerrainGenerationCallbacks(
                AfterNoiseFilled: heights =>
                {
                    ct.ThrowIfCancellationRequested();
                    var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 256);
                    Push("noise", 1, 1, Convert.ToBase64String(png));
                },
                AfterTectonicIteration: resolved.Tectonics.Enabled
                    ? (_, _, heights) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                        Push("tectonic", 1, 1, Convert.ToBase64String(png));
                    }
                    : null,
                AfterThermalIteration: (cur, total, heights) =>
                {
                    ct.ThrowIfCancellationRequested();
                    string? b64 = null;
                    if (ThermalThumbEvery(cur, total))
                    {
                        var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                        b64 = Convert.ToBase64String(png);
                    }

                    Push("thermal", cur, total, b64);
                },
                AfterHydraulicPass: (cur, total, heights) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                    Push("hydraulic", cur, total, Convert.ToBase64String(png));
                },
                AfterThermalRelaxIteration: thermalRelaxTotal > 0
                    ? (cur, total, heights) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        string? b64 = null;
                        if (ThermalThumbEvery(cur, total))
                        {
                            var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                            b64 = Convert.ToBase64String(png);
                        }

                        Push("thermalRelax", cur, total, b64);
                    }
                    : null,
                AfterGlacierIteration: resolved.Glacier.Enabled
                    ? (cur, total, heights) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        string? b64 = null;
                        if (GlacierThumbEvery(cur, total))
                        {
                            var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                            b64 = Convert.ToBase64String(png);
                        }

                        Push("glacier", cur, total, b64);
                    }
                    : null,
                AfterHydrologicalSinkIteration: resolved.Glacier.Enabled
                    ? (_, _, heights) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var png = TerrainPreviewThumbnail.EncodeNearest(heights.AsSpan(), w, h, maxDimension: 384);
                        Push("hydrology", 1, 1, Convert.ToBase64String(png));
                    }
                    : null);

            var generator = new TerrainGenerator();
            var result = generator.Generate(w, h, request.Seed, resolved, callbacks, retainFlatHeightSamplesForExport: true, gpuCompute);

            ct.ThrowIfCancellationRequested();
            byte[] pngFinal = TerrainPreviewEncoding.EncodeHeightmapPng(result);
            string token = Guid.NewGuid().ToString("N");
            previewSessions.Add(token, pngFinal);

            string? lakeMaskToken = null;
            if (resolved.Glacier.Enabled && LakeSinkMaskAny(result.Splatmask.LakeSinkMask))
            {
                lakeMaskToken = Guid.NewGuid().ToString("N");
                previewSessions.AddLakeMask(lakeMaskToken, TerrainLakeMaskPngEncoder.EncodeGrayscale8Png(result.Splatmask.LakeSinkMask));
            }

            string? engineZipToken = null;
            try
            {
                var snap = terrainExportService.CreateSnapshot(result, resolved);
                byte[] zip = terrainExportService.BuildZip(snap,
                    new TerrainExportZipOptions { IncludeObjPreview = true });
                engineZipToken = Guid.NewGuid().ToString("N");
                previewSessions.AddEngineExport(engineZipToken, zip);
            }
            catch
            {
                /* optional bundle — omit token on unexpected failure */
            }

            caller.SendAsync(
                "TerrainComplete",
                new TerrainGenerationCompleteMessage
                {
                    DownloadToken = token,
                    LakeMaskDownloadToken = lakeMaskToken,
                    EngineExportDownloadToken = engineZipToken,
                },
                ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — ignore.
        }
        catch (Exception ex)
        {
            try
            {
                caller.SendAsync("TerrainFailed", new { message = ex.Message }, ct).GetAwaiter().GetResult();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static bool LakeSinkMaskAny(byte[,] mask)
    {
        int h = mask.GetLength(0);
        int w = mask.GetLength(1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask[y, x] != 0)
                    return true;
            }
        }

        return false;
    }
}
