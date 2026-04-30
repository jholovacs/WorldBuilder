namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Tunable parameters for thermal (talus) and hydraulic (particle) erosion passes.
/// Heights are normalized roughly to [0, 1] until <see cref="MaxElevationMeters"/> scaling after erosion.
/// </summary>
public readonly record struct ErosionParams
{
    /// <summary>Meters per cell edge (default: 10 km / 1024 px).</summary>
    public float CellSizeMeters { get; init; }

    /// <summary>Domain frequency for base noise sampling (larger = more detail).</summary>
    public float BaseNoiseFrequency { get; init; }

    /// <summary>Spectral dilation between octaves (typically ~2).</summary>
    public float Lacunarity { get; init; }

    /// <summary>Amplitude decay between octaves (typically ~0.5).</summary>
    public float Persistence { get; init; }

    /// <summary>Number of octaves (default 6).</summary>
    public int Octaves { get; init; }

    /// <summary>
    /// Maximum allowed height difference across one orthogonal step before thermal redistribution applies.
    /// Same units as generated heights (typically normalized [0,1]).
    /// </summary>
    public float ThermalMaxStep { get; init; }

    /// <summary>Strength [0,1] applied to thermal excess redistribution.</summary>
    public float ThermalStrength { get; init; }

    /// <summary>Number of thermal relaxation iterations.</summary>
    public int ThermalIterations { get; init; }

    /// <summary>
    /// Extra thermal talus iterations after hydraulic erosion completes, to relax spikes toward the material angle of repose
    /// (same <see cref="ThermalMaxStep"/> / <see cref="ThermalStrength"/> as the main thermal pass). Zero disables.
    /// </summary>
    public int ThermalRelaxIterationsAfterHydraulic { get; init; }

    /// <summary>How many hydraulic droplets spawn per full grid pass.</summary>
    public int HydraulicDropsPerPass { get; init; }

    /// <summary>Number of outer hydraulic passes over the heightfield.</summary>
    public int HydraulicPasses { get; init; }

    /// <summary>Maximum simulation steps per droplet.</summary>
    public int DropletMaxLifetime { get; init; }

    /// <summary>Blend factor between previous velocity and gradient acceleration [0,1].</summary>
    public float DropletInertia { get; init; }

    /// <summary>Scales sediment carrying capacity vs speed × slope.</summary>
    public float SedimentCapacityFactor { get; init; }

    /// <summary>
    /// Maximum fraction of the bilinear-sampled terrain height that may be eroded in a single droplet step (e.g. 0.1 = 10%).
    /// Zero or negative disables this limit.
    /// </summary>
    public float HydraulicCapacityLimit { get; init; }

    /// <summary>
    /// Upper bound on sediment carrying capacity per step (after speed × slope × <see cref="SedimentCapacityFactor"/>).
    /// Zero or negative means no extra cap.
    /// </summary>
    public float MaxSediment { get; init; }

    /// <summary>Fraction of surplus sediment deposited per step.</summary>
    public float DepositRatio { get; init; }

    /// <summary>
    /// Gaussian σ for hydraulic deposition over a clipped 3×3 neighborhood (&quot;distribution radius&quot; in cell units).
    /// Typical ~0.5–0.7. Zero or omitted resolves via <see cref="TerrainGenerator.ResolveErosionParams"/> to curated defaults.
    /// </summary>
    public float HydraulicDepositDistributionSigmaPx { get; init; }

    /// <summary>Fraction of deficit sediment carved from terrain per step.</summary>
    public float ErodeRatio { get; init; }

    /// <summary>Water retained multiplier per step (1 - evaporation).</summary>
    public float WaterRetention { get; init; }

    /// <summary>Gradient / inertia scaling for droplet acceleration.</summary>
    public float DropletAcceleration { get; init; }

    /// <summary>
    /// After erosion, heights are min–max normalized then multiplied by this value so peaks reach this elevation in meters (default 2000).
    /// </summary>
    public float MaxElevationMeters { get; init; }

    /// <summary>
    /// Absolute elevation in meters used for the underwater mask; cells below this height are flagged as water (default 450).
    /// </summary>
    public float SeaLevelMeters { get; init; }

    /// <summary>
    /// Soft-box vignette depth (meters along each edge): normalized heights taper to zero approaching the rim. Zero disables.
    /// Typical 50 for a traversable apron after erosion/smoothing (applied before meter scaling).
    /// </summary>
    public float MapEdgeSafeZoneFalloffMeters { get; init; }

    /// <summary>Optional alpine glacier incision + till moraines (runs after hydraulic erosion, before meter scaling).</summary>
    public GlacierErosionSettings Glacier { get; init; }

    /// <summary>Optional fault uplift/trench + folding (runs after procedural noise, before thermal erosion).</summary>
    public TectonicUpliftSettings Tectonics { get; init; }

    /// <summary>R / G / B / A biome splat PNG after hydrology and erosion (snow, vegetation proximity, cliffs, sediment).</summary>
    public BiomapGenerationSettings Biomap { get; init; }

    /// <summary>Optional high-contrast L8 foliage scatter PNG (trees) derived from blurred moisture gates + stochastic mask.</summary>
    public VegetationScatterGenerationSettings ScatterVegetation { get; init; }

    /// <summary>
    /// When false (default), base heights blend ridged mountains vs plains fBm using low-frequency Simplex (CPU noise path).
    /// When true, legacy ridged multifractal only (GPU noise allowed).
    /// </summary>
    public bool LandTypeBiomeNoiseDisabled { get; init; }

    /// <summary>Multiplier on pixel coordinates for low-frequency Simplex terrain-type mask (default 1e-4).</summary>
    public float TerrainTypeScale { get; init; }

    /// <summary>fBm persistence on plains / rolling terrain (typically lower than <see cref="Persistence"/>).</summary>
    public float PlainsPersistence { get; init; }

    /// <summary>
    /// Type mask value at which ridged peaks dominate; below this blends toward plains fBm (smoothstep over ~0.2 band below).
    /// </summary>
    public float MountainThreshold { get; init; }

    /// <summary>Scales plains normalized relief vs mountains before biome blend.</summary>
    public float PlainsReliefScale { get; init; }

    /// <summary>
    /// After terrain-type noise: each sample becomes <c>pow(clamp(h,0,1), heightPower)</c> (default 2.2).
    /// Values &gt; 1 flatten lows into broad plains; 1 leaves heights unchanged.
    /// </summary>
    public float HeightPower { get; init; }

    /// <summary>Bilateral smoothing on normalized heights after erosion: range σ scales with this (&gt;0 enables). Typical 0.15–0.8.</summary>
    public float SmoothingStrength { get; init; }

    /// <summary>Gaussian spatial half-support in pixels (window ≈ <c>2×radius + 1</c>). Typical 2–8 for 1024².</summary>
    public float SmoothingRadius { get; init; }

    /// <summary>Sensible defaults for a 1024² heightfield spanning 10 km.</summary>
    public static ErosionParams Default1024Map => new()
    {
        CellSizeMeters = 10_000f / 1024f,
        BaseNoiseFrequency = 4f,
        Lacunarity = 2f,
        Persistence = 0.5f,
        Octaves = 6,
        ThermalMaxStep = 0.035f,
        ThermalStrength = 0.35f,
        ThermalIterations = 24,
        ThermalRelaxIterationsAfterHydraulic = 8,
        HydraulicDropsPerPass = 100_000,
        HydraulicPasses = 3,
        DropletMaxLifetime = 28,
        DropletInertia = 0.65f,
        SedimentCapacityFactor = 4f,
        HydraulicCapacityLimit = 0.1f,
        MaxSediment = 0f,
        DepositRatio = 0.14f,
        HydraulicDepositDistributionSigmaPx = 0.55f,
        ErodeRatio = 0.22f,
        WaterRetention = 0.975f,
        DropletAcceleration = 18f,
        MaxElevationMeters = 2000f,
        SeaLevelMeters = 450f,
        MapEdgeSafeZoneFalloffMeters = 50f,
        Glacier = GlacierErosionSettings.DefaultForUi,
        Tectonics = TectonicUpliftSettings.DefaultForUi,
        Biomap = BiomapGenerationSettings.Curated,
        ScatterVegetation = VegetationScatterGenerationSettings.Disabled,
        TerrainTypeScale = 0.0001f,
        PlainsPersistence = 0.3f,
        MountainThreshold = 0.6f,
        PlainsReliefScale = 0.42f,
        HeightPower = 2.2f,
        SmoothingStrength = 0f,
        SmoothingRadius = 4f,
    };
}

/// <summary>Clamp range for <see cref="ErosionParams.HydraulicDropsPerPass"/> (CPU/GPU hydraulic passes).</summary>
public static class HydraulicDropsPerPassLimits
{
    public const int Min = 20_000;
    public const int Max = 10_000_000;

    public static int Clamp(int dropsPerPass) => Math.Clamp(dropsPerPass, Min, Max);
}
