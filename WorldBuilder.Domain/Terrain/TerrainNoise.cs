using System.Numerics;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Coherent gradient lattice noise + ridged multifractal + optional land-type biome blend (ridged mountains vs plains fBm via Simplex mask).
/// Ridge stacking uses SIMD lanes where possible; gradient noise samples remain scalar per octave × lane.
/// </summary>
internal static class TerrainNoise
{
    internal static void FillRidgedMultifractal(float[] heights, int width, int height, uint seed, in ErosionParams p)
    {
        float lacunarity = p.Lacunarity > 1e-6f ? p.Lacunarity : 2f;
        float persistence = p.Persistence > 1e-6f ? p.Persistence : 0.5f;
        int octaves = Math.Clamp(p.Octaves, 1, 16);
        float baseFreq = p.BaseNoiseFrequency > 1e-6f ? p.BaseNoiseFrequency : 4f;

        int vc = Vector<float>.Count;
        Span<float> coordScratch = stackalloc float[vc];
        Span<float> noiseScratch = stackalloc float[vc];

        float maxMag = 0f;

        for (int y = 0; y < height; y++)
        {
            float ny = (y + 0.5f) / height - 0.5f;
            int row = y * width;
            int x = 0;

            for (; x <= width - vc; x += vc)
            {
                for (int lane = 0; lane < vc; lane++)
                    coordScratch[lane] = ((x + lane) + 0.5f) / width - 0.5f;

                Vector<float> nxVec = new Vector<float>(coordScratch);

                Vector<float> signalSumVec = Vector<float>.Zero;

                float frequency = baseFreq;
                float amplitude = 0.5f;
                Vector<float> weightVec = Vector<float>.One;

                for (int o = 0; o < octaves; o++)
                {
                    Vector<float> sxVec = nxVec * new Vector<float>(frequency * 8f);
                    Vector<float> syVec = new Vector<float>(ny * frequency * 8f);

                    for (int lane = 0; lane < vc; lane++)
                        noiseScratch[lane] = GradientNoise(sxVec[lane], syVec[lane], seed, o);

                    Vector<float> nVec = new Vector<float>(noiseScratch);

                    nVec = Vector.Abs(nVec);
                    nVec = Vector<float>.One - nVec;
                    nVec *= nVec;
                    nVec *= weightVec;
                    weightVec = Vector.Max(nVec, new Vector<float>(1e-6f));

                    signalSumVec += nVec * new Vector<float>(amplitude);

                    frequency *= lacunarity;
                    amplitude *= persistence;
                }

                float stripMax = MaxAcrossLanes(signalSumVec);
                maxMag = Math.Max(maxMag, stripMax);

                signalSumVec.CopyTo(heights.AsSpan(row + x));
            }

            while (x < width)
            {
                float nx = (x + 0.5f) / width - 0.5f;
                float signalSum = RidgedSignalSumScalar(nx, ny, seed, octaves, lacunarity, persistence, baseFreq);
                heights[row + x] = signalSum;
                maxMag = Math.Max(maxMag, signalSum);
                x++;
            }
        }

        NormalizeRidgeBufferByMax(heights, maxMag);
    }

    /// <summary>
    /// Low-frequency Simplex TypeMask × pixel coords blends plains fBm (<see cref="ErosionParams.PlainsPersistence"/>)
    /// vs ridged multifractal; <see cref="ErosionParams.MountainThreshold"/> sets where jagged peaks take over (foothills band below).
    /// </summary>
    internal static void FillLandBiomeBlendedHeightfield(float[] heights, int width, int height, uint seed, in ErosionParams p)
    {
        float lacunarity = p.Lacunarity > 1e-6f ? p.Lacunarity : 2f;
        float ridgePersistence = p.Persistence > 1e-6f ? p.Persistence : 0.5f;
        float plainsPersistence = p.PlainsPersistence > 1e-6f ? p.PlainsPersistence : 0.3f;
        int octaves = Math.Clamp(p.Octaves, 1, 16);
        float baseFreq = p.BaseNoiseFrequency > 1e-6f ? p.BaseNoiseFrequency : 4f;
        float maskScale = p.TerrainTypeScale > 1e-12f ? p.TerrainTypeScale : 0.0001f;
        float plainsRelief = p.PlainsReliefScale > 1e-6f ? p.PlainsReliefScale : 0.42f;

        float mountainThreshold = p.MountainThreshold > 1e-6f ? p.MountainThreshold : 0.6f;
        mountainThreshold = Math.Clamp(mountainThreshold, 0.02f, 0.98f);
        const float foothillsBand = 0.2f;
        float blendLow = Math.Max(0.01f, mountainThreshold - foothillsBand);

        int len = heights.Length;
        var fbmScratch = new float[len];

        float maxRidge = 0f;

        for (int y = 0; y < height; y++)
        {
            float ny = (y + 0.5f) / height - 0.5f;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float nx = (x + 0.5f) / width - 0.5f;
                int i = row + x;

                float ridgeRaw = RidgedSignalSumScalar(nx, ny, seed, octaves, lacunarity, ridgePersistence, baseFreq);
                heights[i] = ridgeRaw;
                fbmScratch[i] = FbmSignalSumScalar(nx, ny, seed, octaves, lacunarity, plainsPersistence, baseFreq);
                maxRidge = Math.Max(maxRidge, ridgeRaw);
            }
        }

        float invMaxRidge = maxRidge > 1e-8f ? 1f / maxRidge : 1f;

        float maxBlended = 0f;
        for (int i = 0; i < len; i++)
        {
            int x = i % width;
            int y = i / width;

            float ridge01 = heights[i] * invMaxRidge;
            float plainsSigned = fbmScratch[i];
            float plains01 = Math.Clamp(plainsSigned * 0.5f + 0.5f, 0f, 1f);

            float mx = (x + 0.5f) * maskScale;
            float my = (y + 0.5f) * maskScale;
            float maskSigned = TerrainSimplexNoise.Sample2D(mx, my, seed ^ 0xBADC0FFEU);
            float typeMask01 = Math.Clamp(maskSigned * 0.5f + 0.5f, 0f, 1f);

            float mountainWeight = Smoothstep(blendLow, mountainThreshold, typeMask01);
            float plainsScaled = plains01 * plainsRelief;
            float blended = Math.Clamp(plainsScaled + (ridge01 - plainsScaled) * mountainWeight, 0f, 1f);
            heights[i] = blended;
            maxBlended = Math.Max(maxBlended, blended);
        }

        NormalizeRidgeBufferByMax(heights, maxBlended);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float d = edge1 - edge0;
        float t = Math.Clamp(d > 1e-8f ? (x - edge0) / d : (x >= edge1 ? 1f : 0f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Standard uncorrelated fBm from stacked gradient noise (mean roughly zero).</summary>
    private static float FbmSignalSumScalar(
        float nx,
        float ny,
        uint seed,
        int octaves,
        float lacunarity,
        float persistence,
        float baseFreq)
    {
        float frequency = baseFreq;
        float amplitude = 0.5f;
        float sum = 0f;
        float norm = 0f;

        for (int o = 0; o < octaves; o++)
        {
            float sx = nx * frequency * 8f;
            float sy = ny * frequency * 8f;
            float n = GradientNoise(sx, sy, seed, o + 17);
            sum += n * amplitude;
            norm += amplitude;

            frequency *= lacunarity;
            amplitude *= persistence;
        }

        return norm > 1e-8f ? sum / norm : 0f;
    }

    /// <summary>
    /// After GPU ridged stacking writes raw ridge sums, normalize using the global maximum (same math as CPU fill).
    /// </summary>
    internal static float ComputeRidgeMaximum(ReadOnlySpan<float> heights)
    {
        float maxMag = 0f;
        for (int i = 0; i < heights.Length; i++)
            maxMag = Math.Max(maxMag, heights[i]);
        return maxMag;
    }

    internal static void NormalizeRidgeBufferByMax(float[] heights, float maxMag)
    {
        int vc = Vector<float>.Count;
        float inv = maxMag > 1e-8f ? 1f / maxMag : 1f;

        int len = heights.Length;
        int i = 0;
        Vector<float> invVec = new Vector<float>(inv);
        for (; i <= len - vc; i += vc)
        {
            Vector<float> v = new Vector<float>(heights.AsSpan(i, vc));
            v *= invVec;
            v = Vector.Min(v, Vector<float>.One);
            v = Vector.Max(v, Vector<float>.Zero);
            v.CopyTo(heights.AsSpan(i));
        }

        while (i < len)
        {
            heights[i] = Math.Clamp(heights[i] * inv, 0f, 1f);
            i++;
        }
    }

    private static float MaxAcrossLanes(Vector<float> v)
    {
        float m = float.MinValue;
        for (int lane = 0; lane < Vector<float>.Count; lane++)
            m = Math.Max(m, v[lane]);
        return m;
    }

    private static float RidgedSignalSumScalar(float nx, float ny, uint seed, int octaves, float lacunarity, float persistence, float baseFreq)
    {
        float frequency = baseFreq;
        float amplitude = 0.5f;
        float weight = 1f;
        float signalSum = 0f;

        for (int o = 0; o < octaves; o++)
        {
            float sx = nx * frequency * 8f;
            float sy = ny * frequency * 8f;

            float n = GradientNoise(sx, sy, seed, o);
            n = MathF.Abs(n);
            n = 1f - n;
            n *= n;
            n *= weight;
            weight = Math.Max(n, 1e-6f);

            signalSum += n * amplitude;

            frequency *= lacunarity;
            amplitude *= persistence;
        }

        return signalSum;
    }

    /// <summary>Deterministic gradient noise in roughly [-1, 1] for domain warp (distinct <paramref name="channel"/>).</summary>
    internal static float DomainWarpNoise(float x, float y, uint seed, int channel)
    {
        uint salted = seed ^ (uint)((channel + 1) * 0x517CC1E7);
        int octave = channel & 15;
        return GradientNoise(x, y, salted, octave);
    }

    /// <summary>Perlin-style lattice gradient noise in roughly [-1, 1].</summary>
    private static float GradientNoise(float x, float y, uint seed, int octave)
    {
        float xf = MathF.Floor(x);
        float yf = MathF.Floor(y);
        int xi = (int)xf;
        int yi = (int)yf;

        float tx = x - xf;
        float ty = y - yf;

        float u = Fade(tx);
        float v = Fade(ty);

        ulong salt = SplitMix64((ulong)(seed ^ (uint)(octave * 977)));
        float g00 = DotCornerGradient(xi, yi, tx, ty, salt ^ 0x9E3779B185EBCA87UL);
        float g10 = DotCornerGradient(xi + 1, yi, tx - 1f, ty, salt ^ 0xC2B2AE3D27741FCDUL);
        float g01 = DotCornerGradient(xi, yi + 1, tx, ty - 1f, salt ^ 0x165667B19E3779F9UL);
        float g11 = DotCornerGradient(xi + 1, yi + 1, tx - 1f, ty - 1f, salt ^ 0xD3A2646C79C819DDUL);

        float ix0 = Lerp(g00, g10, u);
        float ix1 = Lerp(g01, g11, u);
        return Lerp(ix0, ix1, v);
    }

    private static float DotCornerGradient(int ix, int iy, float ox, float oy, ulong cornerSalt)
    {
        ulong cell = (ulong)unchecked((long)ix * 73856093L ^ (long)iy * 19349663L);
        ulong mixed = SplitMix64(cornerSalt ^ cell);
        float angle = (mixed >> 40) / (float)(1 << 24) * (MathF.PI * 2f);
        float gx = MathF.Cos(angle);
        float gy = MathF.Sin(angle);
        return gx * ox + gy * oy;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    internal static float NextFloat01(ref ulong state)
    {
        state = SplitMix64(state);
        return (state >> 40) / (float)(1 << 24);
    }

    internal static ulong SplitMix64(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ z >> 30) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ z >> 27) * 0x94D049BB133111EBUL;
        return z ^ z >> 31;
    }
}
