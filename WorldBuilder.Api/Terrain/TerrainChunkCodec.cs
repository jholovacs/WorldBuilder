namespace WorldBuilder.Api.Terrain;

/// <summary>WBT1 chunk file: magic, header (32 bytes padded), row-major float32 heights.</summary>
public static class TerrainChunkCodec
{
    private static ReadOnlySpan<byte> Magic => "WBT1"u8;

    public static Task WriteChunkAsync(string path, int chunkIndex, int width, int height, ReadOnlyMemory<float> heights, CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (width * height != heights.Length)
            throw new ArgumentException("Height buffer length mismatch.");

        cancellationToken.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write((ushort)1);
            bw.Write(chunkIndex);
            bw.Write(width);
            bw.Write(height);
            bw.Write(0L);
            bw.Write(0);
            bw.Write((short)0);

            foreach (var v in heights.Span)
                bw.Write(v);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllBytesAsync(path, ms.ToArray(), cancellationToken);
    }
}
