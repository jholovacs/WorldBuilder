using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Api.Terrain;

internal static class TerrainPreviewEncoding
{
    internal static byte[] EncodeHeightmapPng(TerrainGenerationResult result)
    {
        int h = result.HeightsMeters.GetLength(0);
        int w = result.HeightsMeters.GetLength(1);
        if (result.FlatHeightSamplesMeters is { Length: var len } mem && len == w * h)
            return TerrainHeightmapPngEncoder.EncodeGrayscale16Png(mem.Span, w, h);

        return TerrainHeightmapPngEncoder.EncodeGrayscale16Png(result.HeightsMeters);
    }
}
