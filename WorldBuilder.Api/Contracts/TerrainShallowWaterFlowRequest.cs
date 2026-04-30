using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Contracts;



/// <summary>Run GPU shallow-water flow simulation on decoded heightmap PNG (normalized samples).</summary>
public sealed class TerrainShallowWaterFlowRequest

{

    /// <summary>Same base64 encoding rules as <see cref="TerrainRefineRequest.HeightmapPngBase64"/>.</summary>

    public required string HeightmapPngBase64 { get; init; }



    /// <summary>Often 300–700; clamps server-side.</summary>

    public int? Iterations { get; init; }



    /// <summary>Normalized initial film depth (thin layer over terrain).</summary>

    public float? InitialWaterDepth { get; init; }

    /// <summary>Vulkan biome kernel after flow — exposes live <see cref="SnowLineMeters"/> to the Angular client.</summary>

    public bool IncludeBiomeRgbaPng { get; init; }

    /// <summary>Optional stochastic seed XOR for <see cref="WorldBuilder.Domain.Terrain.ShallowWaterFlowMapOptions.BiomeScatterNoiseSeed"/> (GPU tree-density <c>W</c>).</summary>
    public uint BiomeScatterNoiseSeed { get; init; }

    /// <summary>Snow elevation threshold (metres absolute) forwarded to biome compute uniforms.</summary>

    public float? SnowLineMeters { get; init; }

    /// <summary>Erosion tuning for sea level, max elevation, and biome softness in compute uniforms.</summary>

    public ErosionParams? ErosionParams { get; init; }

}

