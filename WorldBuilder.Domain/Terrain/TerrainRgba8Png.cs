using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>Encodes WxH RGBA 8-bit PNG for GPU biome previews (outside full <see cref="TerrainBiomapPass"/>).</summary>
public static class TerrainRgba8Png
{
    public static byte[] Encode(ReadOnlySpan<Rgba32> pixelsRowMajor, int width, int height)
    {
        if (pixelsRowMajor.Length != checked(width * height))
            throw new ArgumentException("Pixel count must equal width × height.", nameof(pixelsRowMajor));

        var arr = pixelsRowMajor.ToArray();
        using var img = Image.LoadPixelData<Rgba32>(arr, width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
