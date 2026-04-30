using System.Globalization;
using System.Text;

namespace WorldBuilder.Domain.Terrain;

/// <summary>Minimal Wavefront OBJ (Y-up) for quick meshes in Blender/Unity/Unreal previews.</summary>
public static class TerrainObjMeshExporter
{
    /// <summary>
    /// Builds a capped quad mesh on the XZ plane with Y as elevation. Sampling uses inclusive strides so corners align with the terrain bounds.
    /// </summary>
    public static byte[] Encode(
        float[,] heightsMeters,
        float worldScaleMeters,
        int maxVerticesAlongAxisInclusive = 129)
    {
        ArgumentNullException.ThrowIfNull(heightsMeters);
        if (worldScaleMeters <= 1e-6f)
            throw new ArgumentOutOfRangeException(nameof(worldScaleMeters));
        maxVerticesAlongAxisInclusive = Math.Clamp(maxVerticesAlongAxisInclusive, 8, 2049);

        int vh = heightsMeters.GetLength(0);
        int vw = heightsMeters.GetLength(1);
        int sx = ComputeInclusiveStride(vw, maxVerticesAlongAxisInclusive);
        int sz = ComputeInclusiveStride(vh, maxVerticesAlongAxisInclusive);

        IReadOnlyList<int> xi = BuildAxisSamples(vw, sx);
        IReadOnlyList<int> zi = BuildAxisSamples(vh, sz);
        float ix = vw <= 1 ? 0f : worldScaleMeters / (vw - 1);
        float iz = vh <= 1 ? 0f : worldScaleMeters / (vh - 1);

        static string G(float q) =>
            ((double)q).ToString("G9", CultureInfo.InvariantCulture);

        var sb = new StringBuilder(capacity: 8192);
        sb.AppendLine("# WorldBuilder simplified terrain preview");
        sb.AppendLine("o TerrainPreview");

        int nx = xi.Count;
        int nz = zi.Count;
        var objIndex = new int[nz, nx];
        int counter = 0;
        for (int izp = 0; izp < nz; izp++)
        {
            int z = zi[izp];
            for (int ixp = 0; ixp < nx; ixp++)
            {
                int x = xi[ixp];
                counter++;
                float yElev = heightsMeters[z, x];
                float px = x * ix;
                float pz = z * iz;
                sb.Append('v').Append(' ').Append(G(px)).Append(' ').Append(G(yElev)).Append(' ').Append(G(pz)).AppendLine();
                objIndex[izp, ixp] = counter;
            }
        }

        for (int yi = 0; yi < nz - 1; yi++)
        {
            for (int xiq = 0; xiq < nx - 1; xiq++)
            {
                int i00 = objIndex[yi, xiq];
                int i01 = objIndex[yi, xiq + 1];
                int i10 = objIndex[yi + 1, xiq];
                int i11 = objIndex[yi + 1, xiq + 1];

                sb.Append('f').Append(' ').Append(i00).Append(' ').Append(i10).Append(' ').Append(i11).AppendLine();
                sb.Append('f').Append(' ').Append(i00).Append(' ').Append(i11).Append(' ').Append(i01).AppendLine();
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal static int ComputeInclusiveStride(int vertexCount, int capInclusive)
    {
        capInclusive = Math.Max(8, capInclusive);
        if (vertexCount <= capInclusive)
            return 1;

        int span = vertexCount - 1;
        int buckets = capInclusive - 1;
        return Math.Max(1, (span + buckets - 1) / buckets);
    }

    private static List<int> BuildAxisSamples(int vertexCount1D, int step)
    {
        var xs = new List<int>();
        int v = 0;
        while (true)
        {
            xs.Add(v);
            if (v >= vertexCount1D - 1)
                break;
            v = Math.Min(v + step, vertexCount1D - 1);
        }

        return xs;
    }
}
