using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Terrain;

/// <summary>Nearest-neighbor downsample then 16-bit PNG encode — keeps SignalR payloads small.</summary>
public static class TerrainPreviewThumbnail
{
    /// <summary>Encodes a reduced-resolution PNG suitable for streaming previews.</summary>
    public static byte[] EncodeNearest(ReadOnlySpan<float> heights, int width, int height, int maxDimension)
    {
        maxDimension = Math.Max(64, Math.Min(maxDimension, Math.Max(width, height)));
        int stride = Math.Max(1, (int)Math.Ceiling((double)Math.Max(width, height) / maxDimension));
        int nw = Math.Max(2, (width + stride - 1) / stride);
        int nh = Math.Max(2, (height + stride - 1) / stride);

        var dst = new float[nw * nh];
        int di = 0;
        for (int iy = 0; iy < nh; iy++)
        {
            int sy = Math.Min(iy * stride, height - 1);
            int row = sy * width;
            for (int ix = 0; ix < nw; ix++)
            {
                int sx = Math.Min(ix * stride, width - 1);
                dst[di++] = heights[row + sx];
            }
        }

        return TerrainHeightmapPngEncoder.EncodeGrayscale16Png(dst.AsSpan(), nw, nh);
    }
}
