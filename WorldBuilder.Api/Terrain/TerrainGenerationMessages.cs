namespace WorldBuilder.Api.Terrain;

/// <summary>SignalR payload during streamed terrain generation.</summary>
public sealed class TerrainGenerationProgressMessage
{
    public required string Phase { get; init; }

    /// <summary>1-based step within <see cref="Phase"/>.</summary>
    public int Iteration { get; init; }

    /// <summary>Total steps within current phase.</summary>
    public int IterationTotal { get; init; }

    /// <summary>Overall progress across noise + thermal + hydraulic + finish [0,1].</summary>
    public double Progress01 { get; init; }

    /// <summary>Optional downsampled 16-bit PNG (base64) for live 3D texture updates.</summary>
    public string? PreviewImageBase64 { get; init; }
}

public sealed class TerrainGenerationCompleteMessage
{
    public required string DownloadToken { get; init; }

    /// <summary>Optional second GET token when glacier + depression filling produced a lake mask PNG.</summary>
    public string? LakeMaskDownloadToken { get; init; }

    /// <summary>Optional ZIP with POW2+1 height RAW/PNG, RGBA splat, metadata JSON, optional OBJ preview.</summary>
    public string? EngineExportDownloadToken { get; init; }
}
