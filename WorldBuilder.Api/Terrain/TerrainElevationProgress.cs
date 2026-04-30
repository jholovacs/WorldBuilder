namespace WorldBuilder.Api.Terrain;

/// <summary>Incremental snapshot while iterative uplift runs.</summary>
public readonly record struct TerrainElevationProgress(
    int Iteration,
    double ActualWaterFraction,
    double TargetWaterFraction,
    bool Completed);
