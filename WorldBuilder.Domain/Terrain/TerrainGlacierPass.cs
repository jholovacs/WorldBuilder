namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Mass-balanced ice sheet step + simplified shallow-ice flux, U-shaped distributed incision, and till moraines.
/// Operates on normalized bedrock heights [~0,1] before meter scaling.
/// </summary>
internal static class TerrainGlacierPass
{
    public static void Apply(
        float[] bedrock,
        int width,
        int height,
        in GlacierErosionSettings g,
        uint seed,
        Action<int, int, float[]>? afterIteration)
    {
        int len = checked(width * height);
        var ice = new float[len];
        var till = new float[len];
        InitializeIceMassBalance(bedrock, ice, len, g);

        ulong rng = TerrainNoise.SplitMix64(seed ^ 0x61ACECA71CEUL);

        int iterations = Math.Max(1, g.Iterations);
        float dt = Math.Clamp(g.FlowTimestep, 0.02f, 0.95f);
        float glenExp = Math.Clamp(g.GlenExponent, 1f, 5f);
        float visc = Math.Max(1e-8f, g.ViscosityScale);
        float erosionK = Math.Max(0f, g.BedrockErosionCoefficient);
        float sigma = Math.Max(0.75f, g.ValleyHalfWidthCells);
        int gaussRadius = Math.Min(24, Math.Max(2, (int)Math.Ceiling(sigma * 3f)));

        float tillRate = Math.Clamp(g.TillTransportRate, 0f, 1f);
        float snoutK = Math.Clamp(g.SnoutDepositStrength, 0f, 2f);
        float snoutIceMax = Math.Max(1e-5f, g.SnoutIceThicknessNormalized);

        var surf = new float[len];
        var deltaIce = new float[len];
        var velMag = new float[len];
        var rawIncision = new float[len];
        var bedDelta = new float[len];

        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Clear(deltaIce, 0, len);
            for (int i = 0; i < len; i++)
                surf[i] = bedrock[i] + ice[i];

            ShallowIceFluxStep(ice, surf, deltaIce, width, height, dt, glenExp, visc);

            for (int i = 0; i < len; i++)
            {
                ice[i] = Math.Max(0f, ice[i] + deltaIce[i]);
                surf[i] = bedrock[i] + ice[i];
            }

            VelocityProxy(ice, surf, width, height, velMag);

            Array.Clear(rawIncision, 0, len);
            for (int i = 0; i < len; i++)
            {
                rawIncision[i] = erosionK * ice[i] * velMag[i];
                till[i] += rawIncision[i];
            }

            Array.Clear(bedDelta, 0, len);
            GaussianSpreadRemoval(rawIncision, bedDelta, width, height, gaussRadius, sigma);

            for (int i = 0; i < len; i++)
                bedrock[i] = Math.Max(0f, bedrock[i] + bedDelta[i]);

            TillTransportDownslope(till, bedrock, ice, width, height, tillRate);

            DepositTerminalMoraines(bedrock, till, ice, width, height, snoutIceMax, snoutK, ref rng);

            afterIteration?.Invoke(iter + 1, iterations, bedrock);
        }
    }

    private static void InitializeIceMassBalance(ReadOnlySpan<float> bedrock, Span<float> ice, int len, in GlacierErosionSettings g)
    {
        float snow = g.SnowLineNormalized;
        float melt = g.MeltLineNormalized;
        float acc = Math.Max(0f, g.AccumulationStrength);
        float abl = Math.Max(0f, g.AblationStrength);

        for (int i = 0; i < len; i++)
        {
            float z = bedrock[i];
            float v = 0f;
            if (z > snow)
                v += acc * (z - snow);
            if (z < melt)
                v -= abl * (melt - z);
            ice[i] = Math.Max(0f, v);
        }
    }

    /// <summary>Explicit shallow-ice flux on orthogonal edges (Glen-type conductivity).</summary>
    private static void ShallowIceFluxStep(
        ReadOnlySpan<float> ice,
        ReadOnlySpan<float> surf,
        Span<float> deltaIce,
        int width,
        int height,
        float dt,
        float glenExp,
        float viscScale)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width - 1; x++)
            {
                int i = row + x;
                int j = i + 1;
                float Hi = ice[i];
                float Hj = ice[j];
                float meanH = Math.Max((Hi + Hj) * 0.5f, 1e-8f);
                float Si = surf[i];
                float Sj = surf[j];
                float driving = Si - Sj;
                float flux = viscScale * dt * MathF.Pow(meanH, glenExp) * driving;
                flux = LimitSignedFlux(flux, Hi, Hj, Si, Sj);

                deltaIce[i] -= flux;
                deltaIce[j] += flux;
            }
        }

        for (int y = 0; y < height - 1; y++)
        {
            int row = y * width;
            int rowBelow = row + width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                int j = rowBelow + x;
                float Hi = ice[i];
                float Hj = ice[j];
                float meanH = Math.Max((Hi + Hj) * 0.5f, 1e-8f);
                float Si = surf[i];
                float Sj = surf[j];
                float driving = Si - Sj;
                float flux = viscScale * dt * MathF.Pow(meanH, glenExp) * driving;
                flux = LimitSignedFlux(flux, Hi, Hj, Si, Sj);

                deltaIce[i] -= flux;
                deltaIce[j] += flux;
            }
        }

        static float LimitSignedFlux(float flux, float Hi, float Hj, float Si, float Sj)
        {
            if (flux >= 0f)
                return Math.Min(flux, Math.Max(Hi * 0.49f, 0f));
            return Math.Max(flux, -Math.Max(Hj * 0.49f, 0f));
        }
    }

    /// <summary>Velocity proxy ~ ice thickness × surface slope magnitude.</summary>
    private static void VelocityProxy(
        ReadOnlySpan<float> ice,
        ReadOnlySpan<float> surf,
        int width,
        int height,
        Span<float> velOut)
    {
        for (int i = 0; i < velOut.Length; i++)
            velOut[i] = 0f;

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                float gx = (surf[i + 1] - surf[i - 1]) * 0.5f;
                float gy = (surf[i + width] - surf[i - width]) * 0.5f;
                float slopeMag = MathF.Sqrt(gx * gx + gy * gy);
                velOut[i] = slopeMag * Math.Max(ice[i], 1e-8f);
            }
        }
    }

    /// <summary>Distribute bedrock removal mass with a Gaussian footprint for broad valleys.</summary>
    private static void GaussianSpreadRemoval(
        ReadOnlySpan<float> rawIncision,
        Span<float> bedDelta,
        int width,
        int height,
        int radius,
        float sigma)
    {
        float invTwoSigma2 = 1f / Math.Max(2f * sigma * sigma, 1e-8f);

        for (int cy = 0; cy < height; cy++)
        {
            for (int cx = 0; cx < width; cx++)
            {
                int i = cy * width + cx;
                float M = rawIncision[i];
                if (M < 1e-12f)
                    continue;

                float sumW = 0f;
                int rMin = Math.Max(-radius, -cx);
                int rMax = Math.Min(radius, width - 1 - cx);
                int dMin = Math.Max(-radius, -cy);
                int dMax = Math.Min(radius, height - 1 - cy);

                for (int dy = dMin; dy <= dMax; dy++)
                {
                    for (int dx = rMin; dx <= rMax; dx++)
                    {
                        float rr = dx * dx + dy * dy;
                        sumW += MathF.Exp(-rr * invTwoSigma2);
                    }
                }

                if (sumW < 1e-12f)
                    continue;

                for (int dy = dMin; dy <= dMax; dy++)
                {
                    int ny = cy + dy;
                    int nrow = ny * width;
                    for (int dx = rMin; dx <= rMax; dx++)
                    {
                        int nx = cx + dx;
                        float rr = dx * dx + dy * dy;
                        float w = MathF.Exp(-rr * invTwoSigma2) / sumW;
                        bedDelta[nrow + nx] -= M * w;
                    }
                }
            }
        }
    }

    private static void TillTransportDownslope(
        Span<float> till,
        float[] bedrock,
        float[] ice,
        int width,
        int height,
        float rate)
    {
        var tillDelta = new float[till.Length];

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                float t = till[i];
                if (t < 1e-12f)
                    continue;

                float surfHere = bedrock[i] + ice[i];
                float bestSurf = surfHere;
                int best = i;

                void Consider(int ni)
                {
                    float s = bedrock[ni] + ice[ni];
                    if (s < bestSurf - 1e-9f)
                    {
                        bestSurf = s;
                        best = ni;
                    }
                }

                Consider(i - 1);
                Consider(i + 1);
                Consider(i - width);
                Consider(i + width);

                if (best == i)
                    continue;

                float slope = surfHere - bestSurf;
                float move = Math.Min(t, rate * t * Math.Clamp(slope * 6f, 0f, 1f));
                tillDelta[i] -= move;
                tillDelta[best] += move;
            }
        }

        for (int i = 0; i < till.Length; i++)
            till[i] = Math.Max(0f, till[i] + tillDelta[i]);
    }

    private static void DepositTerminalMoraines(
        float[] bedrock,
        Span<float> till,
        ReadOnlySpan<float> ice,
        int width,
        int height,
        float thinIce,
        float snoutStrength,
        ref ulong rng)
    {
        int len = bedrock.Length;
        var deposit = new float[len];

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                float Hi = ice[i];
                if (Hi > thinIce || till[i] < 1e-9f)
                    continue;

                float nbIce = Math.Max(
                    Math.Max(ice[i - 1], ice[i + 1]),
                    Math.Max(ice[i - width], ice[i + width]));

                if (nbIce <= Hi + 1e-6f)
                    continue;

                float dep = Math.Min(till[i], snoutStrength * till[i] * nbIce);
                if (dep <= 1e-12f)
                    continue;

                deposit[i] += dep * (0.92f + 0.08f * TerrainNoise.NextFloat01(ref rng));
                till[i] -= dep;
                till[i] = Math.Max(0f, till[i]);
            }
        }

        for (int i = 0; i < len; i++)
            bedrock[i] = Math.Max(0f, bedrock[i] + deposit[i]);
    }
}
