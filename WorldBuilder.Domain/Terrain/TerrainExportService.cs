using System.IO.Compression;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace WorldBuilder.Domain.Terrain;

/// <summary>Produces a ZIP bundle Unity/Unreal can import (<c>POW2+1</c> heightmap RAW/PNG, RGBA splat, metadata, optional OBJ).</summary>
public sealed class TerrainExportService
{
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TerrainExportSnapshot CreateSnapshot(TerrainGenerationResult result, ErosionParams resolvedAlready) =>
        TerrainExportSnapshot.Capture(result, resolvedAlready, worldScaleMeters: 10_000f);

    public byte[] BuildZip(TerrainExportSnapshot snapshot, TerrainExportZipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new TerrainExportZipOptions();

        float[,] h = snapshot.HeightsMeters;
        MinMax(h, out float qMin, out float qMax);

        byte[] png = TerrainHeightmapPngEncoder.EncodeGrayscale16Png(h);
        byte[] raw = TerrainHeightmapPngEncoder.EncodeUInt16LittleEndianRaw(h);
        byte[] splat = EncodeSplatRgbaPng(h, snapshot.SplatmaskSourceResolution, snapshot.WorldScaleMeters);

        var meta = new TerrainEngineExportMetadata
        {
            WorldScale = snapshot.WorldScaleMeters,
            MaxElevation = snapshot.ErosionParamsUsed.MaxElevationMeters,
            SeaLevel = snapshot.ErosionParamsUsed.SeaLevelMeters,
            HeightmapWidth = snapshot.VertexWidth,
            HeightmapHeight = snapshot.VertexHeight,
            CellSizeMeters = snapshot.VertexWidth <= 1
                ? snapshot.WorldScaleMeters
                : snapshot.WorldScaleMeters / (snapshot.VertexWidth - 1),
            HeightQuantizationMinMeters = qMin,
            HeightQuantizationMaxMeters = qMax,
        };

        string metaJson = JsonSerializer.Serialize(meta, MetadataJson);

        using var zipMs = new MemoryStream();
        using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "heightmap.png", png);
            AddEntry(zip, "heightmap.raw", raw);
            AddEntry(zip, "splat_rgba.png", splat);

            byte[]? bio = snapshot.BiomapRgbaPngSourceResolution;
            if (bio is { Length: > 0 })
                AddEntry(zip, "biomap_rgba.png", bio);

            byte[]? scatter = snapshot.VegetationScatterMaskPngSourceResolution;
            if (scatter is { Length: > 0 })
                AddEntry(zip, "vegetation_scatter.png", scatter);

            byte[] metaBytes = System.Text.Encoding.UTF8.GetBytes(metaJson);
            AddEntry(zip, "metadata.json", metaBytes);

            if (options.IncludeObjPreview)
            {
                byte[] obj = TerrainObjMeshExporter.Encode(
                    h,
                    snapshot.WorldScaleMeters,
                    options.ObjMaxVerticesPerAxisInclusive);
                AddEntry(zip, "terrain_preview.obj", obj);
            }
        }

        return zipMs.ToArray();
    }

    private static void MinMax(float[,] heights, out float min, out float max)
    {
        int h = heights.GetLength(0);
        int w = heights.GetLength(1);
        min = float.MaxValue;
        max = float.MinValue;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = heights[y, x];
                if (v < min)
                    min = v;
                if (v > max)
                    max = v;
            }
        }

        if (min > max)
        {
            min = 0f;
            max = 1f;
        }
    }

    private static byte[] EncodeSplatRgbaPng(
        float[,] heightsPow2Plus1,
        TerrainSplatmask splatOriginal,
        float worldScaleMeters)
    {
        int vw = heightsPow2Plus1.GetLength(1);
        int vh = heightsPow2Plus1.GetLength(0);
        float cell = vw <= 1 ? worldScaleMeters : worldScaleMeters / Math.Max(vw - 1, 1);

        var flow =
            vw == splatOriginal.Width && vh == splatOriginal.Height
                ? CopyByte(splatOriginal.FlowMask)
                : TerrainGridResampler.UpsampleBilinearByte(splatOriginal.FlowMask, vw, vh);

        var waterMask =
            vw == splatOriginal.Width && vh == splatOriginal.Height
                ? CopyByte(splatOriginal.WaterMask)
                : TerrainGridResampler.UpsampleBilinearByte(splatOriginal.WaterMask, vw, vh);

        var lakeSink =
            vw == splatOriginal.Width && vh == splatOriginal.Height
                ? CopyByte(splatOriginal.LakeSinkMask)
                : TerrainGridResampler.UpsampleBilinearByte(splatOriginal.LakeSinkMask, vw, vh);

        GradientStats(heightsPow2Plus1, cell, out float[,] slopeMag, out float maxSlopeUsed);

        var pixels = new Rgba32[checked(vw * vh)];
        float maxSlopeDenom = Math.Max(maxSlopeUsed, 1e-8f);
        float flatRef = Math.Max(maxSlopeDenom * 0.12f, 1e-8f);

        int k = 0;
        for (int y = 0; y < vh; y++)
        {
            for (int x = 0; x < vw; x++)
            {
                float sm = slopeMag[y, x];
                float slopeN = Math.Clamp(sm / maxSlopeDenom, 0f, 1f);
                byte r = (byte)Math.Clamp((int)Math.Round(slopeN * 255f), 0, 255);

                byte g = flow[y, x];

                int bVal = Math.Max(waterMask[y, x], lakeSink[y, x]);
                byte b = (byte)Math.Clamp(bVal, 0, 255);

                float waterCue = b / 255f;
                float flowCue = g / 255f;
                float dryness = 1f - Math.Clamp(waterCue + 0.35f * flowCue, 0f, 1f);
                float flat = Math.Clamp(1f - sm / flatRef, 0f, 1f);
                byte a = (byte)Math.Clamp((int)Math.Round(flat * dryness * 255f), 0, 255);

                pixels[k++] = new Rgba32(r, g, b, a);
            }
        }

        using var image = Image.LoadPixelData<Rgba32>(pixels, vw, vh);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static void GradientStats(
        float[,] h,
        float cellMeters,
        out float[,] slopeMag,
        out float maxSlope)
    {
        int vh = h.GetLength(0);
        int vw = h.GetLength(1);
        slopeMag = new float[vh, vw];
        maxSlope = 1e-8f;
        float inv2c = 1f / (2f * Math.Max(cellMeters, 1e-8f));

        for (int y = 0; y < vh; y++)
        {
            for (int x = 0; x < vw; x++)
            {
                int xm = x > 0 ? x - 1 : x;
                int xp = x < vw - 1 ? x + 1 : x;
                int ym = y > 0 ? y - 1 : y;
                int yp = y < vh - 1 ? y + 1 : y;

                float gx = (h[y, xp] - h[y, xm]) * inv2c;
                float gy = (h[yp, x] - h[ym, x]) * inv2c;
                float mag = MathF.Sqrt(gx * gx + gy * gy);
                slopeMag[y, x] = mag;
                if (mag > maxSlope)
                    maxSlope = mag;
            }
        }
    }

    private static byte[,] CopyByte(byte[,] src)
    {
        int h = src.GetLength(0);
        int w = src.GetLength(1);
        var dst = new byte[h, w];
        Array.Copy(src, dst, dst.Length);
        return dst;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream es = e.Open();
        es.Write(bytes, 0, bytes.Length);
    }
}

/// <seealso cref="TerrainExportService.BuildZip"/>
public sealed class TerrainExportZipOptions
{
    public bool IncludeObjPreview { get; init; }

    public int ObjMaxVerticesPerAxisInclusive { get; init; } = 129;
}
