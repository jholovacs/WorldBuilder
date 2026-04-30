using System.Collections.Generic;
using System.Numerics;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Recursive midpoint displacement along a chord plus helpers to resolve strike endpoints on the centered terrain square.
/// Coordinates match <see cref="TerrainTectonicPass"/> centered normalized domain [-½,½]².
/// </summary>
public static class FaultLineGenerator
{
    /// <summary>Half-extent of the procedural domain in normalized coords.</summary>
    public const float NormalizedHalfExtent = 0.5f;

    /// <summary>
    /// Strike exits centered square [<paramref name="halfExtent"/>]² along fault strike — endpoints ordered along +strike direction.
    /// </summary>
    public static bool TryStrikeChordThroughCenterSquare(float strikeDegrees, float halfExtent, out Vector2 start, out Vector2 end)
    {
        float θ = strikeDegrees * (MathF.PI / 180f);
        Vector2 dir = new(MathF.Cos(θ), MathF.Sin(θ));
        Span<Vector2> hits = stackalloc Vector2[8];
        int n = CollectSquareLineIntersections(dir, halfExtent, hits);
        if (n < 2)
        {
            start = default;
            end = default;
            return false;
        }

        int ai = 0, bi = 1;
        float bestSq = Vector2.DistanceSquared(hits[ai], hits[bi]);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float dsq = Vector2.DistanceSquared(hits[i], hits[j]);
                if (dsq > bestSq)
                {
                    bestSq = dsq;
                    ai = i;
                    bi = j;
                }
            }
        }

        Vector2 a = hits[ai];
        Vector2 b = hits[bi];
        if (Vector2.Dot(a, dir) <= Vector2.Dot(b, dir))
        {
            start = a;
            end = b;
        }
        else
        {
            start = b;
            end = a;
        }

        return true;
    }

    /// <summary>
    /// Midpoint subdivision: each iteration inserts displaced midpoints for every segment; <paramref name="roughness"/> halves each pass.
    /// </summary>
    public static void GenerateRecursiveMidpointDisplacement(
        Vector2 start,
        Vector2 end,
        int iterations,
        float roughness,
        ref ulong rng,
        List<Vector2> path)
    {
        path.Clear();
        path.Add(start);
        path.Add(end);

        float r = Math.Clamp(roughness, 0f, 1f);
        int iter = Math.Clamp(iterations, 0, 14);

        List<Vector2> buf = new((1 << iter) + 8);

        List<Vector2> cur = path;
        List<Vector2> next = buf;

        for (int pass = 0; pass < iter; pass++)
        {
            if (r <= 1e-12f)
                break;

            next.Clear();
            int cc = cur.Count;
            next.Capacity = Math.Max(next.Capacity, cc * 2 - 1);
            next.Add(cur[0]);

            for (int i = 0; i < cc - 1; i++)
            {
                Vector2 va = cur[i];
                Vector2 vb = cur[i + 1];
                Vector2 mid = (va + vb) * 0.5f;
                Vector2 seg = vb - va;
                float lenSq = seg.LengthSquared();
                if (lenSq > 1e-18f)
                {
                    float len = MathF.Sqrt(lenSq);
                    Vector2 perp = new Vector2(-seg.Y, seg.X) / len;
                    float u = TerrainNoise.NextFloat01(ref rng);
                    float displacement = (u - 0.5f) * len * r;
                    mid += perp * displacement;
                }

                next.Add(mid);
            }

            next.Add(cur[cc - 1]);

            (cur, next) = (next, cur);
        }

        if (!ReferenceEquals(cur, path))
        {
            path.Clear();
            path.AddRange(cur);
        }
    }

    /// <summary>
    /// Signed perpendicular distance (cross-based vs closest segment, consistent with infinite strike normal)
    /// and arc length from first vertex along the polyline to the closest approach.
    /// </summary>
    public static void SamplePolylineFault(
        Vector2 sample,
        ReadOnlySpan<Vector2> vertices,
        out float signedPerpendicular,
        out float arcLengthAlongPath)
    {
        signedPerpendicular = 0f;
        arcLengthAlongPath = 0f;

        int vn = vertices.Length;
        if (vn < 2)
            return;

        float bestDistSq = float.MaxValue;
        float signedBest = 0f;
        float arcBest = 0f;

        float arcPrefix = 0f;

        for (int i = 0; i < vn - 1; i++)
        {
            Vector2 va = vertices[i];
            Vector2 vb = vertices[i + 1];
            Vector2 ab = vb - va;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-18f)
                continue;

            Vector2 ap = sample - va;
            float t = Vector2.Dot(ap, ab) / lenSq;
            float tClamped = Math.Clamp(t, 0f, 1f);
            Vector2 closest = va + tClamped * ab;
            float distSq = Vector2.DistanceSquared(sample, closest);

            float len = MathF.Sqrt(lenSq);
            float signed = (ab.X * ap.Y - ab.Y * ap.X) / len;
            float arcHere = arcPrefix + tClamped * len;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                signedBest = signed;
                arcBest = arcHere;
            }

            arcPrefix += len;
        }

        signedPerpendicular = signedBest;
        arcLengthAlongPath = arcBest;
    }

    private static int CollectSquareLineIntersections(Vector2 dir, float half, Span<Vector2> hits)
    {
        float dx = dir.X;
        float dy = dir.Y;
        int n = 0;

        const float eps = 1e-8f;

        if (Math.Abs(dx) > eps)
        {
            TryAddSquareIntersection(dx, dy, half, half / dx, hits, ref n);
            TryAddSquareIntersection(dx, dy, half, -half / dx, hits, ref n);
        }

        if (Math.Abs(dy) > eps)
        {
            TryAddSquareIntersection(dx, dy, half, half / dy, hits, ref n);
            TryAddSquareIntersection(dx, dy, half, -half / dy, hits, ref n);
        }

        return n;
    }

    private static void TryAddSquareIntersection(float dx, float dy, float half, float t, Span<Vector2> hits, ref int n)
    {
        float x = t * dx;
        float y = t * dy;
        if (Math.Abs(x) > half + 1e-5f || Math.Abs(y) > half + 1e-5f)
            return;

        Vector2 p = new Vector2(x, y);
        for (int i = 0; i < n; i++)
        {
            if (Vector2.DistanceSquared(p, hits[i]) < 1e-12f)
                return;
        }

        hits[n++] = p;
    }
}
