using Microsoft.AspNetCore.Mvc;
using WorldBuilder.Api.Contracts;
using WorldBuilder.Api.Storage;
using WorldBuilder.Domain.Simulation;
using WorldBuilder.Domain.WorldShapes;
using WorldBuilder.Domain.Worlds;

namespace WorldBuilder.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WorldsController : ControllerBase
{
    private readonly IWorldDefinitionStore _store;

    public WorldsController(IWorldDefinitionStore store)
    {
        _store = store;
    }

    [HttpGet("creation-defaults")]
    public ActionResult<WorldCreationDefaultsResponse> GetCreationDefaults([FromQuery] double surfaceAreaKm2 = WorldPhysicalDefaults.DefaultSurfaceAreaKm2)
    {
        if (surfaceAreaKm2 <= 0 || double.IsNaN(surfaceAreaKm2) || double.IsInfinity(surfaceAreaKm2))
            return BadRequest("surfaceAreaKm2 must be positive.");

        var suggested = WorldPhysicalDefaults.SuggestedElevationsFromSurfaceAreaKm2(surfaceAreaKm2);
        var cells = WorldPhysicalDefaults.EstimatedSurfaceCellCount(surfaceAreaKm2, WorldPhysicalDefaults.DefaultGroundResolutionSquareMeters);

        var response = new WorldCreationDefaultsResponse
        {
            SurfaceAreaKm2 = surfaceAreaKm2,
            SurfaceWaterCoveragePercent = WorldPhysicalDefaults.DefaultSurfaceWaterCoveragePercent,
            GroundResolutionSquareMeters = WorldPhysicalDefaults.DefaultGroundResolutionSquareMeters,
            LinearScaleVersusEarth = suggested.LinearScaleVersusEarth,
            MaxGroundElevationAboveSeaLevelMeters = suggested.MaxGroundElevationAboveSeaLevelMeters,
            MinGroundDepthBelowSeaLevelMeters = suggested.MinGroundDepthBelowSeaLevelMeters,
            MedianGroundElevationAboveSeaLevelMeters = suggested.MedianGroundElevationAboveSeaLevelMeters,
            EstimatedSurfaceCellCount = cells,
            SphereEquivalentRadiusMeters = WorldPhysicalDefaults.SphereRadiusMetersFromSurfaceAreaKm2(surfaceAreaKm2),
        };

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorldSummary>>> List(CancellationToken cancellationToken)
    {
        var worlds = await _store.ListAsync(cancellationToken);
        return Ok(worlds);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorldDefinition>> Get(Guid id, CancellationToken cancellationToken)
    {
        var world = await _store.GetAsync(id, cancellationToken);
        return world is null ? NotFound() : Ok(world);
    }

    [HttpPost]
    public async Task<ActionResult<WorldDefinition>> Create([FromBody] CreateWorldRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");

        var surfaceKm2 = request.TotalSurfaceAreaSquareKilometers > 0
            ? request.TotalSurfaceAreaSquareKilometers
            : WorldPhysicalDefaults.DefaultSurfaceAreaKm2;

        if (double.IsNaN(surfaceKm2) || double.IsInfinity(surfaceKm2))
            return BadRequest("TotalSurfaceAreaSquareKilometers is invalid.");

        var waterPct = ClampWaterCoverage(request.SurfaceWaterCoveragePercent);

        var resolution = request.GroundResolutionSquareMeters > 0
            ? request.GroundResolutionSquareMeters
            : WorldPhysicalDefaults.DefaultGroundResolutionSquareMeters;

        var suggested = WorldPhysicalDefaults.SuggestedElevationsFromSurfaceAreaKm2(surfaceKm2);

        var maxEl = request.MaxGroundElevationAboveSeaLevelMeters ?? suggested.MaxGroundElevationAboveSeaLevelMeters;
        var minDepth = request.MinGroundDepthBelowSeaLevelMeters ?? suggested.MinGroundDepthBelowSeaLevelMeters;
        var medianEl = request.MedianGroundElevationAboveSeaLevelMeters ?? suggested.MedianGroundElevationAboveSeaLevelMeters;

        WorldPhysicalDefaults.ApplyShapeGeometry(
            request.Shape,
            surfaceKm2,
            out var referenceRadius,
            out var torusMajor,
            out var torusMinor,
            out var patchX,
            out var patchZ);

        var cellCount = WorldPhysicalDefaults.EstimatedSurfaceCellCount(surfaceKm2, resolution);

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var seed = request.Seed ?? (ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue);

        var definition = new WorldDefinition
        {
            Id = id,
            Name = request.Name.Trim(),
            Shape = request.Shape,
            Seed = seed,
            TotalSurfaceAreaSquareKilometers = surfaceKm2,
            SurfaceWaterCoveragePercent = waterPct,
            MaxGroundElevationAboveSeaLevelMeters = maxEl,
            MinGroundDepthBelowSeaLevelMeters = minDepth,
            MedianGroundElevationAboveSeaLevelMeters = medianEl,
            GroundResolutionSquareMeters = resolution,
            EstimatedSurfaceCellCount = cellCount,
            LinearScaleVersusEarth = suggested.LinearScaleVersusEarth,
            ReferenceRadiusMeters = referenceRadius,
            TorusMajorRadiusMeters = torusMajor,
            TorusMinorRadiusMeters = torusMinor,
            PatchExtentXMeters = patchX,
            PatchExtentZMeters = patchZ,
            LastCompletedPhase = SimulationPhase.BaseTopology.ToString(),
            CreatedUtc = now,
            ModifiedUtc = now,
        };

        await _store.SaveAsync(definition, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = definition.Id }, definition);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WorldDefinition>> Update(Guid id, [FromBody] UpdateWorldRequest request, CancellationToken cancellationToken)
    {
        var existing = await _store.GetAsync(id, cancellationToken);
        if (existing is null)
            return NotFound();

        existing.Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim();
        if (!string.IsNullOrWhiteSpace(request.LastCompletedPhase))
            existing.LastCompletedPhase = request.LastCompletedPhase!;
        existing.ModifiedUtc = DateTimeOffset.UtcNow;

        await _store.SaveAsync(existing, cancellationToken);
        return Ok(existing);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var removed = await _store.DeleteAsync(id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }

    private static double ClampWaterCoverage(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            return WorldPhysicalDefaults.DefaultSurfaceWaterCoveragePercent;
        return Math.Clamp(percent, 0d, 100d);
    }
}
