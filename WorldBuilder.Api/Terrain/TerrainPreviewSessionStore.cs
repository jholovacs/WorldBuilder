namespace WorldBuilder.Api.Terrain;

/// <summary>Single-use PNG bytes for GET preview-download endpoints.</summary>
public sealed class TerrainPreviewSessionStore
{
    private readonly Dictionary<string, byte[]> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _lakeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _engineExports = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void Add(string token, byte[] pngBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(pngBytes);
        lock (_sync)
            _sessions[token] = pngBytes;
    }

    public bool TryTake(string token, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? pngBytes)
    {
        pngBytes = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var bytes))
                return false;
            _sessions.Remove(token);
            pngBytes = bytes;
            return true;
        }
    }

    /// <summary>8-bit grayscale lake-mask PNG.</summary>
    public void AddLakeMask(string token, byte[] pngBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(pngBytes);
        lock (_sync)
            _lakeSessions[token] = pngBytes;
    }

    public bool TryTakeLakeMask(string token, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? pngBytes)
    {
        pngBytes = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_sync)
        {
            if (!_lakeSessions.TryGetValue(token, out var bytes))
                return false;
            _lakeSessions.Remove(token);
            pngBytes = bytes;
            return true;
        }
    }

    /// <summary>Unity/Unreal engine bundle (ZIP — heightmap RAW/PNG, splat PNG, metadata, optional OBJ).</summary>
    public void AddEngineExport(string token, byte[] zipBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(zipBytes);
        lock (_sync)
            _engineExports[token] = zipBytes;
    }

    public bool TryTakeEngineExport(string token, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? zipBytes)
    {
        zipBytes = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_sync)
        {
            if (!_engineExports.TryGetValue(token, out var bytes))
                return false;
            _engineExports.Remove(token);
            zipBytes = bytes;
            return true;
        }
    }
}
