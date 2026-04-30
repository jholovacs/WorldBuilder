using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace WorldBuilder.Domain.Terrain;

/// <summary>
/// Applies uplift/trench asymmetry along a midpoint-displaced fault polyline (domain-warped sampling) plus folding ripples confined near the fault trace.
/// </summary>
internal static class TerrainTectonicPass
{
    private const float TwoPi = MathF.PI * 2f;

    public static void Apply(
        Span<float> heights,
        int width,
        int height,
        in TectonicUpliftSettings p,
        uint seed)
    {
        float upliftPeak = Math.Max(0f, p.UpliftPeakNormalized);
        float upliftSigma = Math.Max(1e-4f, p.UpliftFalloffNormalized);
        float trenchDepth = Math.Max(0f, p.TrenchDepthNormalized);
        float trenchSigma = Math.Max(1e-4f, p.TrenchFalloffNormalized);
        float foldAmp = Math.Max(0f, p.FoldingAmplitudeNormalized);
        float foldCycles = Math.Max(0.25f, p.FoldingCyclesAcrossStrike);
        float foldEnvSigma = Math.Max(1e-4f, p.FoldingEnvelopeFalloffNormalized);

        float warpScale = Math.Max(0f, p.FaultWarpNoiseScale);
        float warpAmp = Math.Max(0f, p.FaultWarpAmplitudeNormalized);
        bool warpOn = warpScale > 1e-8f && warpAmp > 1e-8f;

        ulong rng = TerrainNoise.SplitMix64(seed ^ 0xFEEDC0DECAFEUL);
        float phaseOff = TerrainNoise.NextFloat01(ref rng) * TwoPi;

        float foldPhaseScale = foldCycles * TwoPi;

        var faultPath = new List<Vector2>(128);
        ulong faultRng = TerrainNoise.SplitMix64(seed ^ 0xFAC775005CAFEUL);
        int faultIterations = Math.Clamp(p.FaultPathIterations, 0, 14);

        if (!FaultLineGenerator.TryStrikeChordThroughCenterSquare(
                p.FaultAngleDegrees,
                FaultLineGenerator.NormalizedHalfExtent,
                out Vector2 chordStart,
                out Vector2 chordEnd))
        {
            float θ = p.FaultAngleDegrees * (MathF.PI / 180f);
            Vector2 dir = new(MathF.Cos(θ), MathF.Sin(θ));
            float h = FaultLineGenerator.NormalizedHalfExtent;
            float ax = Math.Abs(dir.X);
            float ay = Math.Abs(dir.Y);
            float s = h / Math.Max(Math.Max(ax, ay), 1e-8f);
            chordStart = -s * dir;
            chordEnd = s * dir;
        }

        FaultLineGenerator.GenerateRecursiveMidpointDisplacement(
            chordStart,
            chordEnd,
            faultIterations,
            p.FaultPathRoughness,
            ref faultRng,
            faultPath);

        ReadOnlySpan<Vector2> faultVertices = CollectionsMarshal.AsSpan(faultPath);

        int len = heights.Length;
        for (int i = 0; i < len; i++)
        {
            int x = i % width;
            int y = i / width;

            float nxCell = (x + 0.5f) / width - 0.5f;
            float nyCell = (y + 0.5f) / height - 0.5f;

            float wx = nxCell;
            float wy = nyCell;
            if (warpOn)
            {
                float sx = nxCell * warpScale;
                float sy = nyCell * warpScale;
                wx += TerrainNoise.DomainWarpNoise(sx, sy, seed, 0) * warpAmp;
                wy += TerrainNoise.DomainWarpNoise(sx, sy, seed, 1) * warpAmp;
            }

            var warpedSample = new Vector2(wx, wy);
            FaultLineGenerator.SamplePolylineFault(warpedSample, faultVertices, out float u, out float along);

            if (p.InvertOverridingSide)
                u = -u;

            float upliftTerm = u >= 0f ? upliftPeak * Gaussian(u, upliftSigma) : 0f;
            float trenchTerm = u < 0f ? -trenchDepth * Gaussian(u, trenchSigma) : 0f;

            float envelope = GaussianMag(u, foldEnvSigma);
            float folding = foldAmp * envelope * MathF.Sin(along * foldPhaseScale + phaseOff);

            float v = heights[i] + upliftTerm + trenchTerm + folding;
            heights[i] = Math.Max(0f, v);
        }
    }

    private static float Gaussian(float signedPerpU, float sigma)
    {
        float z = signedPerpU / sigma;
        return MathF.Exp(-z * z);
    }

    /// <summary>|u| Gaussian — strongest at fault trace.</summary>
    private static float GaussianMag(float signedPerpU, float sigma)
    {
        float a = MathF.Abs(signedPerpU);
        float z = a / sigma;
        return MathF.Exp(-z * z);
    }
}
