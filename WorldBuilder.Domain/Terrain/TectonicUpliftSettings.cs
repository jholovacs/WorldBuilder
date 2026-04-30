namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Optional plate-boundary deformation applied after procedural noise and before thermal/hydraulic erosion.
/// Uses centered normalized coordinates — heights remain in generator normalized space.
/// </summary>
public readonly record struct TectonicUpliftSettings
{
    /// <summary>When false, no tectonics run.</summary>
    public bool Enabled { get; init; }

    /// <summary>Strike direction of the fault trace measured from +X (degrees).</summary>
    public float FaultAngleDegrees { get; init; }

    /// <summary>
    /// Flip which half-plane receives uplift vs trench — swaps signed perpendicular distance sign before masking.
    /// </summary>
    public bool InvertOverridingSide { get; init; }

    /// <summary>Peak uplift magnitude on the overriding side (Gaussian perpendicular decay).</summary>
    public float UpliftPeakNormalized { get; init; }

    /// <summary>σ for uplift Gaussian perpendicular to fault (same units as normalized coords).</summary>
    public float UpliftFalloffNormalized { get; init; }

    /// <summary>Peak trench depth magnitude on the subducting side (positive number subtracted).</summary>
    public float TrenchDepthNormalized { get; init; }

    /// <summary>σ for trench Gaussian perpendicular to fault.</summary>
    public float TrenchFalloffNormalized { get; init; }

    /// <summary>Amplitude of folding sine ripples parallel to strike.</summary>
    public float FoldingAmplitudeNormalized { get; init; }

    /// <summary>Approximate full sine-wave cycles visible across strike projection span.</summary>
    public float FoldingCyclesAcrossStrike { get; init; }

    /// <summary>σ controlling how tightly folding is confined near the fault (perpendicular).</summary>
    public float FoldingEnvelopeFalloffNormalized { get; init; }

    /// <summary>
    /// Noise coordinate scale before sampling warp offsets (applied to centered normalized coords).
    /// Zero or <see cref="FaultWarpAmplitudeNormalized"/> zero yields a straight fault trace.
    /// </summary>
    public float FaultWarpNoiseScale { get; init; }

    /// <summary>Warp displacement magnitude in normalized grid coords (same units as uplift σ).</summary>
    public float FaultWarpAmplitudeNormalized { get; init; }

    /// <summary>
    /// Recursive midpoint displacement passes along the strike chord across the normalized square (typical 6–8 for ~10 km previews).
    /// Zero yields only chord endpoints (straight fault trace).
    /// </summary>
    public int FaultPathIterations { get; init; }

    /// <summary>
    /// Per-pass perpendicular jitter factor [0, 1]; displacement scales as <c>(random − ½) × segment length × roughness</c>, then roughness halves each iteration.
    /// </summary>
    public float FaultPathRoughness { get; init; }

    public static TectonicUpliftSettings Disabled => default;

    /// <summary>Moderate convergent boundary suitable for ~1024² previews.</summary>
    public static TectonicUpliftSettings PlateBoundaryPreset => new()
    {
        Enabled = true,
        FaultAngleDegrees = 38f,
        InvertOverridingSide = false,
        UpliftPeakNormalized = 0.14f,
        UpliftFalloffNormalized = 0.22f,
        TrenchDepthNormalized = 0.07f,
        TrenchFalloffNormalized = 0.06f,
        FoldingAmplitudeNormalized = 0.009f,
        FoldingCyclesAcrossStrike = 18f,
        FoldingEnvelopeFalloffNormalized = 0.08f,
        FaultWarpNoiseScale = 14f,
        FaultWarpAmplitudeNormalized = 0.038f,
        FaultPathIterations = 7,
        FaultPathRoughness = 0.42f,
    };

    /// <summary>
    /// Same geometry as <see cref="PlateBoundaryPreset"/> with <see cref="Enabled"/> false — used in API/UI defaults so clients receive non-zero sliders before enabling.
    /// </summary>
    public static TectonicUpliftSettings DefaultForUi => PlateBoundaryPreset with { Enabled = false };
}
