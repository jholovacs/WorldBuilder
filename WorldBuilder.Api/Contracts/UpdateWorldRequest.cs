namespace WorldBuilder.Api.Contracts;

public sealed class UpdateWorldRequest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional bump when advancing simulation phases.</summary>
    public string? LastCompletedPhase { get; set; }
}
