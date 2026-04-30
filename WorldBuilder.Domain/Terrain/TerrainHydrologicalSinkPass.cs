namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Priority-queue depression filling on the normalized heightfield (before meter scaling).
/// Raises sinks toward spill elevations from domain boundaries and emits an 8-bit lake-depth mask.
/// </summary>
internal static class TerrainHydrologicalSinkPass
{
    private static readonly (int Dx, int Dy)[] Neighbors8 =
    {
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    };

    /// <summary>
    /// Modifies <paramref name="heights"/> in place with filled elevations; builds row-major-compatible <paramref name="lakeMask8"/> [y,x].
    /// </summary>
    public static void Apply(float[] heights, int width, int height, out byte[,] lakeMask8)
    {
        int len = checked(width * height);
        lakeMask8 = new byte[height, width];

        var original = new float[len];
        heights.AsSpan().CopyTo(original);

        var spill = new float[len];
        for (int i = 0; i < len; i++)
            spill[i] = float.PositiveInfinity;

        var pq = new PriorityQueue<int, float>();

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (x != 0 && y != 0 && x != width - 1 && y != height - 1)
                    continue;

                int idx = row + x;
                spill[idx] = heights[idx];
                pq.Enqueue(idx, spill[idx]);
            }
        }

        const float eps = 1e-5f;

        while (pq.TryDequeue(out int idx, out float pri))
        {
            if (pri > spill[idx] + eps)
                continue;

            int x = idx % width;
            int y = idx / width;

            foreach (var (dx, dy) in Neighbors8)
            {
                int nx = x + dx;
                int ny = y + dy;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                    continue;

                int j = ny * width + nx;
                float cand = Math.Max(heights[j], pri);
                if (cand + eps < spill[j])
                {
                    spill[j] = cand;
                    pq.Enqueue(j, cand);
                }
            }
        }

        float maxDelta = 0f;
        for (int i = 0; i < len; i++)
        {
            float d = spill[i] - original[i];
            if (d > maxDelta)
                maxDelta = d;
            heights[i] = spill[i];
        }

        float invMax = maxDelta > 1e-10f ? 1f / maxDelta : 0f;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float d = spill[i] - original[i];
                lakeMask8[y, x] = d > eps
                    ? (byte)Math.Clamp(Math.Round(d * invMax * 255f), 0d, 255d)
                    : (byte)0;
            }
        }
    }
}
