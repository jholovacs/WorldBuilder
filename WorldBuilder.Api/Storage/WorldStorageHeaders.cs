using Microsoft.AspNetCore.Http;

namespace WorldBuilder.Api.Storage;
/// <summary>Client sends chosen world root via this header (Base64-encoded UTF-8 path).</summary>
public static class WorldStorageHeaders
{
    public const string ClientRootBase64 = "X-World-Storage-Root";
}

public static class StoragePathResolver
{
    public static bool TryDecodeClientRoot(string? base64Header, out string? absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(base64Header))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(base64Header.Trim());
            var decoded = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            if (string.IsNullOrEmpty(decoded))
                return false;

            var full = Path.GetFullPath(decoded);
            if (!Path.IsPathRooted(full))
                return false;

            absolutePath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Server default when the client does not send <see cref="WorldStorageHeaders.ClientRootBase64"/>.</summary>
    public static string ResolveConfiguredWorldRoot(in WorldStorageOptions options, string contentRootPath)
    {
        var configured = options.RootPath;
        var combined = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRootPath, configured);
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Effective world-files directory: client-chosen path from the header when present, otherwise <see cref="WorldStorageOptions.RootPath"/> (relative to content root when not absolute).
    /// </summary>
    public static string ResolveWorldFilesRoot(HttpContext? httpContext, in WorldStorageOptions options, string contentRootPath)
    {
        var headers = httpContext?.Request.Headers;
        if (headers is not null &&
            headers.TryGetValue(WorldStorageHeaders.ClientRootBase64, out var raw) &&
            TryDecodeClientRoot(raw.ToString(), out var clientRoot) &&
            clientRoot is not null)
            return clientRoot;

        return ResolveConfiguredWorldRoot(in options, contentRootPath);
    }
}