using System.Buffers.Binary;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Encodes meter-valued heightmaps as single-channel 16-bit grayscale PNG or raw UInt16 LE (same linear quantization as PNG).
/// </summary>
public static class TerrainHeightmapPngEncoder
{
    /// <inheritdoc cref="EncodeGrayscale16Png(ReadOnlySpan{float},int,int)"/>
    public static byte[] EncodeGrayscale16Png(float[,] heightsMeters)
    {
        int height = heightsMeters.GetLength(0);
        int width = heightsMeters.GetLength(1);
        var flat = new float[checked(width * height)];
        int idx = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                flat[idx++] = heightsMeters[y, x];
        }

        return EncodeGrayscale16Png(flat.AsSpan(), width, height);
    }

    /// <summary>
    /// Row-major flat samples (<paramref name="width"/> × <paramref name="height"/>): linear tile min/max → UInt16 full range.
    /// </summary>
    public static byte[] EncodeGrayscale16Png(ReadOnlySpan<float> heightsMeters, int width, int height)
    {
        Quantize(heightsMeters, width, height, out var pixels);

        using var image = Image.LoadPixelData<L16>(pixels, width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <inheritdoc cref="EncodeUInt16LittleEndianRaw(ReadOnlySpan{float},int,int)"/>
    public static byte[] EncodeUInt16LittleEndianRaw(float[,] heightsMeters)
    {
        int height = heightsMeters.GetLength(0);
        int width = heightsMeters.GetLength(1);
        var flat = new float[checked(width * height)];
        int idx = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                flat[idx++] = heightsMeters[y, x];
        }

        return EncodeUInt16LittleEndianRaw(flat.AsSpan(), width, height);
    }

    /// <summary>Same quantization as PNG: UInt16 LE row-major.</summary>
    public static byte[] EncodeUInt16LittleEndianRaw(ReadOnlySpan<float> heightsMeters, int width, int height)
    {
        Quantize(heightsMeters, width, height, out var pixels);

        var bytes = new byte[checked(pixels.Length * 2)];
        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), pixels[i].PackedValue);

        return bytes;
    }

    private static void Quantize(ReadOnlySpan<float> heightsMeters, int width, int height, out L16[] pixels)
    {
        if (heightsMeters.Length != checked(width * height))
            throw new ArgumentException("Span length must equal width × height.");

        float minH = float.MaxValue;
        float maxH = float.MinValue;
        for (int i = 0; i < heightsMeters.Length; i++)
        {
            float v = heightsMeters[i];
            if (v < minH)
                minH = v;
            if (v > maxH)
                maxH = v;
        }

        float range = Math.Max(maxH - minH, 1e-8f);
        pixels = new L16[width * height];
        for (int i = 0; i < heightsMeters.Length; i++)
        {
            float t = (heightsMeters[i] - minH) / range;
            ushort q = (ushort)Math.Clamp(Math.Round(t * 65535f), 0d, 65535d);
            pixels[i] = new L16(q);
        }
    }

    /// <summary>
    /// Inverse of <see cref="Quantize"/> pixel ordering — row-major floats ~[0,1] suitable as thermal/hydraulic input (matches generator pre-meter heights).
    /// </summary>
    public static float[] DecodeGrayscale16PngToNormalized(ReadOnlySpan<byte> pngBytes, out int width, out int height)
    {
        using var ms = new MemoryStream(pngBytes.ToArray());

        using var image = Image.Load<L16>(ms);
        int w = image.Width;
        int h = image.Height;
        if (w < 4 || h < 4)
            throw new ArgumentException("Heightmap dimensions must be at least 4×4.");

        width = w;
        height = h;

        var flat = new float[checked(w * h)];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<L16> row = accessor.GetRowSpan(y);
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                    flat[rowOffset + x] = row[x].PackedValue / 65535f;
            }
        });

        return flat;
    }
}
