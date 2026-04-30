namespace WorldBuilder.Api.SignalR;

/// <summary>
/// Serialized to SignalR clients (camelCase). Describes long-running terrain jobs with optional coarse progress [0,1].
/// </summary>
public sealed class TerrainProgressMessage
{
    public string JobKind { get; set; } = "terrainGeneration";

    /// <summary>e.g. preparing | uplifting | writingChunks | savingManifest | complete | failed</summary>
    public string Phase { get; set; } = "";

    public Guid WorldId { get; set; }

    public string? WorldName { get; set; }

    public string Message { get; set; } = "";

    /// <summary>Optional coarse progress within the job (0–1).</summary>
    public double? Progress01 { get; set; }

    public int? CurrentChunk { get; set; }

    public int? TotalChunks { get; set; }

    public int? Iteration { get; set; }

    public double? ActualWaterFraction { get; set; }

    public double? TargetWaterFraction { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}
