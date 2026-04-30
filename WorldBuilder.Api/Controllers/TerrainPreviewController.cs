using Microsoft.AspNetCore.Mvc;
using WorldBuilder.Api.Contracts;
using WorldBuilder.Api.Storage;
using WorldBuilder.Api.Terrain;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Controllers;

/// <summary>
/// Procedural terrain preview — square heightmaps, scaling metadata, and persisted previews (no world scope).
/// </summary>
[ApiController]
[Route("api/terrain")]
public sealed class TerrainPreviewController : ControllerBase
{
    private readonly ITerrainMapStore _terrainMaps;
    private readonly TerrainPreviewSessionStore _previewSessions;
    private readonly ITerrainGpuCompute _gpuCompute;

    public TerrainPreviewController(
        ITerrainMapStore terrainMaps,
        TerrainPreviewSessionStore previewSessions,
        ITerrainGpuCompute gpuCompute)
    {
        _terrainMaps = terrainMaps;
        _previewSessions = previewSessions;
        _gpuCompute = gpuCompute;
    }

    /// <summary>Full default <see cref="ErosionParams"/> for client-side merge before preview/save.</summary>
    [HttpGet("defaults")]
    public ActionResult<ErosionParams> GetErosionDefaults()
    {
        return Ok(ErosionParams.Default1024Map);
    }

    /// <summary>Generate a 16-bit PNG heightmap with explicit erosion tuning (not persisted).</summary>
    [HttpPost("preview")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult PreviewTerrain([FromBody] TerrainPreviewRequest body)
    {
        if (body is null)
            return BadRequest();

        var resolved = TerrainGenerator.ResolveErosionParams(body.ErosionParams);
        var generator = new TerrainGenerator();
        var result = generator.Generate(
            TerrainGenerator.DefaultResolution,
            TerrainGenerator.DefaultResolution,
            body.Seed,
            resolved,
            callbacks: null,
            retainFlatHeightSamplesForExport: true,
            gpuCompute: _gpuCompute);

        var png = TerrainPreviewEncoding.EncodeHeightmapPng(result);
        return File(png, "image/png", "preview.png");
    }

    /// <summary>
    /// Hydraulic erosion on decoded heights (GPU or CPU), optional post-hydro thermal relaxation and glacier segment —
    /// skips redoing the initial thermal pass because the PNG already reflects it from full generation.
    /// </summary>
    [HttpPost("refine")]
    [RequestSizeLimit(20_000_000)]
    public IActionResult RefineTerrain([FromBody] TerrainRefineRequest body)
    {
        if (body is null)
            return BadRequest();

        byte[] pngBytes;
        try
        {
            pngBytes = Convert.FromBase64String(body.HeightmapPngBase64);
        }
        catch (FormatException)
        {
            return BadRequest("heightmapPngBase64 is not valid base64.");
        }

        float[] normalized;
        int w;
        int h;
        try
        {
            normalized = TerrainHeightmapPngEncoder.DecodeGrayscale16PngToNormalized(pngBytes, out w, out h);
        }
        catch (Exception ex)
        {
            return BadRequest($"Could not decode heightmap PNG: {ex.Message}");
        }

        var resolved = TerrainGenerator.ResolveErosionParams(body.ErosionParams);
        var generator = new TerrainGenerator();
        var result = generator.ContinueErosion(
            normalized,
            w,
            h,
            resolved,
            body.HydraulicSeed,
            callbacks: null,
            retainFlatHeightSamplesForExport: true,
            gpuCompute: _gpuCompute,
            skipInitialThermalIteration: true);

        var png = TerrainPreviewEncoding.EncodeHeightmapPng(result);
        return File(png, "image/png", "terrain-refined.png");
    }

    /// <summary>
    /// GPU shallow-water simulation (<c>ShallowWaterFlux.spv</c> / <c>ShallowWaterUpdate.spv</c>) plus optional
    /// <c>CalculateBiomes.spv</c> unified pack (<c>R</c>/<c>G</c>/<c>B</c>: snow–veg–cliffs; <c>W</c>: GPU tree-density with Worley × white-noise jitter).
    /// Returns <c>image/png</c> (16-bit flow) unless <paramref name="body"/>.IncludeBiomeRgbaPng —
    /// then JSON <see cref="TerrainShallowWaterFlowResponse"/> (<c>flowPngBase64</c>, <c>biomeRgbaPngBase64</c>).
    /// </summary>
    [HttpPost("shallow-water-flow-map")]
    [RequestSizeLimit(20_000_000)]
    public IActionResult ShallowWaterFlowMap([FromBody] TerrainShallowWaterFlowRequest body)
    {
        if (body is null)
            return BadRequest();

        byte[] pngBytes;
        try
        {
            pngBytes = Convert.FromBase64String(body.HeightmapPngBase64);
        }
        catch (FormatException)
        {
            return BadRequest("heightmapPngBase64 is not valid base64.");
        }

        float[] normalized;
        int w;
        int h;
        try
        {
            normalized = TerrainHeightmapPngEncoder.DecodeGrayscale16PngToNormalized(pngBytes, out w, out h);
        }
        catch (Exception ex)
        {
            return BadRequest($"Could not decode heightmap PNG: {ex.Message}");
        }

        if (w != TerrainGenerator.DefaultResolution || h != TerrainGenerator.DefaultResolution)
            return BadRequest($"Heightmap must be {TerrainGenerator.DefaultResolution}×{TerrainGenerator.DefaultResolution}; got {w}×{h}.");

        int iterations = Math.Clamp(body.Iterations ?? 500, 1, 2000);
        float depth = Math.Clamp(body.InitialWaterDepth ?? 0.02f, 1e-5f, 0.35f);

        var tuned = TerrainGenerator.ResolveErosionParams(body.ErosionParams ?? default);
        var biomapMerged = TerrainBiomapPass.Resolved(tuned.Biomap);

        ShallowWaterFlowMapOptions? flowOpts =
            body.IncludeBiomeRgbaPng
                ? new ShallowWaterFlowMapOptions
                {
                    IncludeBiomeRgbaPng = true,
                    SnowLineMeters = body.SnowLineMeters ?? biomapMerged.SnowLineMeters,
                    ErosionTuning = body.ErosionParams,
                    BiomeScatterNoiseSeed = body.BiomeScatterNoiseSeed,
                }
                : null;

        if (!_gpuCompute.TryShallowWaterFlowMapPng(
                normalized.AsSpan(),
                w,
                h,
                out byte[]? flowPng,
                out byte[]? biomePng,
                iterations,
                depth,
                flowOpts)
            || flowPng is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Shallow-water GPU path unavailable (Vulkan + ShallowWater*.spv) or simulation failed.");
        }

        if (body.IncludeBiomeRgbaPng)
        {
            var response = new TerrainShallowWaterFlowResponse
            {
                FlowPngBase64 = Convert.ToBase64String(flowPng),
                BiomeRgbaPngBase64 = biomePng is null ? null : Convert.ToBase64String(biomePng),
            };

            return Ok(response);
        }

        return File(flowPng, "image/png", "terrain-shallow-water-flow.png");
    }

    /// <summary>
    /// Live preview or cached PNG when <paramref name="mapName"/> identifies a saved map.
    /// Otherwise generates a square heightmap (<see cref="TerrainGenerator.DefaultResolution"/>²).
    /// Use <c>?format=raw</c> for UInt16 little-endian binary (same quantization as PNG).
    /// </summary>
    /// <param name="mapName">Optional persisted map — skips regeneration.</param>
    /// <param name="seed">RNG seed when not loading by map name.</param>
    /// <param name="format"><c>png</c> (default) or <c>raw</c>.</param>
    [HttpGet("heightmap")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult GetHeightmap([FromQuery] string? mapName = null, [FromQuery] uint seed = 42, [FromQuery] string format = "png")
    {
        if (!string.IsNullOrWhiteSpace(mapName))
        {
            if (!_terrainMaps.TryGetHeightmapFullPath(mapName, out var cachedPath))
                return NotFound();

            var safeName = $"{mapName.Trim().ToLowerInvariant()}.heightmap.png";
            return PhysicalFile(cachedPath, "image/png", safeName);
        }

        var generator = new TerrainGenerator();
        var result = generator.Generate(TerrainGenerator.DefaultResolution, TerrainGenerator.DefaultResolution, seed, gpuCompute: _gpuCompute);

        var fmt = format.Trim().ToLowerInvariant();
        if (fmt is "raw" or "uint16" or "binary")
        {
            var raw = TerrainHeightmapPngEncoder.EncodeUInt16LittleEndianRaw(result.HeightsMeters);
            return File(raw, "application/octet-stream", "heightmap.raw");
        }

        if (fmt is not "" and not "png")
            return BadRequest("format must be png or raw.");

        var png = TerrainPreviewEncoding.EncodeHeightmapPng(result);
        return File(png, "image/png", "heightmap.png");
    }

    /// <summary>Returns resolved default erosion scaling so clients can match mesh vertical scale and cell spacing.</summary>
    [HttpGet("metadata")]
    public ActionResult<TerrainMetadataResponse> GetMetadata()
    {
        var p = TerrainGenerator.ResolveErosionParams();
        return Ok(new TerrainMetadataResponse(
            p.CellSizeMeters,
            p.MaxElevationMeters,
            p.SeaLevelMeters,
            p.HeightPower,
            p.TerrainTypeScale,
            p.PlainsPersistence,
            p.MountainThreshold,
            p.SmoothingStrength,
            p.SmoothingRadius,
            p.HydraulicDepositDistributionSigmaPx));
    }

    /// <summary>Persist procedural terrain identity plus a 16-bit grayscale PNG heightmap.</summary>
    [HttpPost("save")]
    [RequestSizeLimit(25_000_000)]
    public async Task<ActionResult<TerrainMapSavedResponse>> SaveTerrainMap(
        [FromBody] SaveTerrainMapRequest body,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.MapName))
            return BadRequest("mapName is required.");

        var resolved = TerrainGenerator.ResolveErosionParams(body.ErosionParams ?? default);
        byte[] png;

        if (!string.IsNullOrWhiteSpace(body.HeightmapPngBase64))
        {
            try
            {
                png = Convert.FromBase64String(body.HeightmapPngBase64.Trim());
            }
            catch (FormatException)
            {
                return BadRequest("heightmapPngBase64 is not valid base64.");
            }

            try
            {
                _ = TerrainHeightmapPngEncoder.DecodeGrayscale16PngToNormalized(png, out var w, out var h);
                if (w != TerrainGenerator.DefaultResolution || h != TerrainGenerator.DefaultResolution)
                {
                    return BadRequest(
                        $"Heightmap must be {TerrainGenerator.DefaultResolution}×{TerrainGenerator.DefaultResolution}; got {w}×{h}.");
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not decode heightmap PNG: {ex.Message}");
            }
        }
        else
        {
            var generator = new TerrainGenerator();
            var result = generator.Generate(
                TerrainGenerator.DefaultResolution,
                TerrainGenerator.DefaultResolution,
                body.Seed,
                resolved,
                callbacks: null,
                retainFlatHeightSamplesForExport: true,
                gpuCompute: _gpuCompute);

            png = TerrainPreviewEncoding.EncodeHeightmapPng(result);
        }

        var map = new TerrainMap(body.MapName.Trim(), body.Seed, resolved);

        var savedUtc = await _terrainMaps.SaveAsync(map, png, TerrainGenerator.DefaultResolution, cancellationToken).ConfigureAwait(false);

        return Ok(new TerrainMapSavedResponse(savedUtc.CanonicalMapName, savedUtc.SavedUtc));
    }

    /// <summary>Single-use binary returned after SignalR <c>TerrainComplete</c> with <paramref name="token"/>.</summary>
    [HttpGet("preview-download/{token}")]
    public IActionResult DownloadPreview(string token)
    {
        if (!_previewSessions.TryTake(token, out var pngBytes))
            return NotFound();

        return File(pngBytes, "image/png", "terrain-preview.png");
    }

    /// <summary>Single-use 8-bit grayscale lake mask PNG when <c>TerrainComplete.lakeMaskDownloadToken</c> is present.</summary>
    [HttpGet("lake-mask-download/{token}")]
    public IActionResult DownloadLakeMask(string token)
    {
        if (!_previewSessions.TryTakeLakeMask(token, out var pngBytes))
            return NotFound();

        return File(pngBytes, "image/png", "lake-mask.png");
    }

    /// <summary>
    /// Single-use ZIP returned after SignalR <c>TerrainComplete</c> when <paramref name="token"/> references the engine bundle.
    /// </summary>
    [HttpGet("engine-export-download/{token}")]
    public IActionResult DownloadEngineExport(string token)
    {
        if (!_previewSessions.TryTakeEngineExport(token, out var zipBytes))
            return NotFound();

        return File(zipBytes, "application/zip", "terrain-engine-export.zip");
    }

    /// <summary>List saved terrain previews (metadata only — heights served from PNG).</summary>
    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyList<TerrainMapListEntry>>> ListTerrainMaps(CancellationToken cancellationToken)
    {
        var rows = await _terrainMaps.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(rows);
    }
}
