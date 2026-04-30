using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>Single-channel 8-bit PNG encoding for lake / hydrological masks.</summary>
public static class TerrainLakeMaskPngEncoder
{
    public static byte[] EncodeGrayscale8Png(byte[,] mask)
    {
        int height = mask.GetLength(0);
        int width = mask.GetLength(1);
        var pixels = new byte[width * height];
        int k = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                pixels[k++] = mask[y, x];
        }

        using var image = Image.LoadPixelData<L8>(pixels, width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
