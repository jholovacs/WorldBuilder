namespace WorldBuilder.Domain.Terrain;

/// <summary>Optional glacier dynamics + till transport before heights are scaled to meters.</summary>
public readonly record struct GlacierErosionSettings
{
    /// <summary>When false, no glacier math runs.</summary>
    public bool Enabled { get; init; }

    /// <summary>Normalized bedrock height above which snow accumulation adds ice mass.</summary>
    public float SnowLineNormalized { get; init; }

    /// <summary>Normalized bedrock height below which melting removes ice mass.</summary>
    public float MeltLineNormalized { get; init; }

    /// <summary>Ice thickness gained per unit height above <see cref="SnowLineNormalized"/> at initialization.</summary>
    public float AccumulationStrength { get; init; }

    /// <summary>Ice thickness lost per unit depth below <see cref="MeltLineNormalized"/> at initialization.</summary>
    public float AblationStrength { get; init; }

    /// <summary>Combined outer iterations (each = ice flow + carve + till).</summary>
    public int Iterations { get; init; }

    /// <summary>Explicit timestep factor for shallow-ice flux (smaller = stabler).</summary>
    public float FlowTimestep { get; init; }

    /// <summary>Glen-type exponent on ice thickness for flux (typically ~3).</summary>
    public float GlenExponent { get; init; }

    /// <summary>Scales ice flux magnitude — larger behaves like lower basal viscosity.</summary>
    public float ViscosityScale { get; init; }

    /// <summary>Bedrock incision strength × ice thickness × velocity proxy.</summary>
    public float BedrockErosionCoefficient { get; init; }

    /// <summary>Gaussian half-width (cells) for distributing incision → U-shaped cross-section.</summary>
    public float ValleyHalfWidthCells { get; init; }

    /// <summary>Fraction of local till moved downslope each iteration.</summary>
    public float TillTransportRate { get; init; }

    /// <summary>Strength for dumping till onto bedrock where ice is thin (moraine).</summary>
    public float SnoutDepositStrength { get; init; }

    /// <summary>Normalized ice thickness below which snout deposition is evaluated.</summary>
    public float SnoutIceThicknessNormalized { get; init; }

    /// <summary>Sensible defaults — disabled unless <see cref="Enabled"/> is set.</summary>
    public static GlacierErosionSettings Disabled => default;

    /// <summary>Moderate alpine glacier carving suitable for ~1024² previews.</summary>
    public static GlacierErosionSettings AlpinePreset => new()
    {
        Enabled = true,
        SnowLineNormalized = 0.62f,
        MeltLineNormalized = 0.38f,
        AccumulationStrength = 0.085f,
        AblationStrength = 0.055f,
        Iterations = 28,
        FlowTimestep = 0.22f,
        GlenExponent = 3f,
        ViscosityScale = 0.038f,
        BedrockErosionCoefficient = 0.018f,
        ValleyHalfWidthCells = 2.6f,
        TillTransportRate = 0.32f,
        SnoutDepositStrength = 0.11f,
        SnoutIceThicknessNormalized = 0.018f,
    };

    /// <summary>
    /// Same tuning as <see cref="AlpinePreset"/> with <see cref="Enabled"/> false — used in API/UI defaults so clients receive meaningful slider defaults before enabling.
    /// </summary>
    public static GlacierErosionSettings DefaultForUi => AlpinePreset with { Enabled = false };
}
