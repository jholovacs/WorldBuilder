namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Checkpoints during <see cref="TerrainGenerator.Generate"/>; height buffers are live — callers must clone before async work.
/// </summary>
public sealed record TerrainGenerationCallbacks(
    Action<float[]>? AfterNoiseFilled = null,
    Action<int, int, float[]>? AfterTectonicIteration = null,
    Action<int, int, float[]>? AfterThermalIteration = null,
    /// <summary>Thermal relaxation after hydraulic erosion (same talus model; separate hook for progress reporting).</summary>
    Action<int, int, float[]>? AfterThermalRelaxIteration = null,
    Action<int, int, float[]>? AfterHydraulicPass = null,
    Action<int, int, float[]>? AfterGlacierIteration = null,
    Action<int, int, float[]>? AfterHydrologicalSinkIteration = null);
