using Microsoft.AspNetCore.Mvc;
using WorldBuilder.Api.Terrain;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Controllers;

[ApiController]
[Route("api/worlds/{worldId:guid}/terrain")]
public sealed class TerrainController : ControllerBase
{
    private readonly TerrainGenerationService _terrain;

    public TerrainController(TerrainGenerationService terrain)
    {
        _terrain = terrain;
    }

    [HttpGet("manifest")]
    public async Task<ActionResult<TerrainManifestDocument>> GetManifest(Guid worldId, CancellationToken cancellationToken)
    {
        var manifest = await _terrain.GetManifestAsync(worldId, cancellationToken);
        return manifest is null ? NotFound() : Ok(manifest);
    }

    [HttpPost("ocean-baseline")]
    public async Task<ActionResult<TerrainManifestDocument>> EnsureOceanBaseline(Guid worldId, CancellationToken cancellationToken)
    {
        var manifest = await _terrain.EnsureOceanBaselineAsync(worldId, cancellationToken);
        return Ok(manifest);
    }

    public sealed class GenerateTerrainBody
    {
        public int ChunkCount { get; set; }
    }

    [HttpPost("generate")]
    public async Task<ActionResult<TerrainGenerationJobResult>> Generate(Guid worldId, [FromBody] GenerateTerrainBody body, CancellationToken cancellationToken)
    {
        if (body.ChunkCount < TerrainGenerationLimits.MinChunkCount || body.ChunkCount > TerrainGenerationLimits.MaxChunkCount)
            return BadRequest($"chunkCount must be between {TerrainGenerationLimits.MinChunkCount} and {TerrainGenerationLimits.MaxChunkCount}.");

        try
        {
            var result = await _terrain.GenerateTerrainAsync(worldId, body.ChunkCount, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("chunks/{chunkIndex:int}")]
    public IActionResult GetChunk(Guid worldId, int chunkIndex)
    {
        if (!_terrain.TryGetChunkFilePath(worldId, chunkIndex, out var path))
            return NotFound();

        return PhysicalFile(path!, "application/octet-stream", Path.GetFileName(path));
    }
}
