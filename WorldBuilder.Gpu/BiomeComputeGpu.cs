using System.Runtime.CompilerServices;

using System.Runtime.InteropServices;



using WorldBuilder.Domain.Terrain;



namespace WorldBuilder.Gpu;



/// <summary>CPU slope prep for <see cref="BiomeComputeShaderWrapper"/> (matches TerrainBiomapPass-style gradient).</summary>

internal static class BiomeComputeGpu

{

    internal static void ComputeSlopeDegrees(

        ReadOnlySpan<float> heightNormRowMajor,

        int width,

        int height,

        float cellSizeMeters,

        float maxElevationMeters,

        Span<float> slopeDegOutRowMajor)

    {

        int count = checked(width * height);

        if (heightNormRowMajor.Length != count || slopeDegOutRowMajor.Length != count)

            throw new ArgumentException("Span lengths must equal width × height.");



        float inv2c = 1f / (2f * Math.Max(cellSizeMeters, 1e-6f));

        float em = Math.Max(maxElevationMeters, 1e-6f);



        for (int y = 0; y < height; y++)

        {

            int row = y * width;

            int ym = Math.Max(y - 1, 0);

            int yp = Math.Min(y + 1, height - 1);

            for (int x = 0; x < width; x++)

            {

                int xm = Math.Max(x - 1, 0);

                int xp = Math.Min(x + 1, width - 1);

                float gx =

                    (heightNormRowMajor[row + xp] * em - heightNormRowMajor[row + xm] * em) * inv2c;

                float gy =

                    (heightNormRowMajor[yp * width + x] * em - heightNormRowMajor[ym * width + x] * em) *

                    inv2c;



                float mag = MathF.Sqrt(gx * gx + gy * gy);

                slopeDegOutRowMajor[row + x] = MathF.Atan(mag) * (180f / MathF.PI);

            }

        }

    }

}



/// <summary>Scalar-layout match for <see cref="Shaders"/> <c>BiomeCb</c> (<c>CalculateBiomes.hlsl</c>).</summary>

[StructLayout(LayoutKind.Sequential)]

internal struct BiomeParamsGpu

{

    public uint Width;

    public uint Height;

    public float SnowLineMeters;

    public float SeaLevelMeters;



    public float MaxElevationMeters;

    public float SnowSoftBandMeters;

    public float CliffSlopeDegrees;

    public float CliffSlopeSoftDegrees;



    public float VegWaterScale;

    public float MoistDepthScale;

    public float TreeMoistureThreshold;

    public float TreeMaxSlopeDegrees;



    public float WorleyScalePx;

    public float FlowAccumInvMax;

    public uint NoiseSeed;

    /// <summary>Normalized hydraulic / shallow-water depth; wetlands use <see cref="WetlandThreshold"/>.</summary>
    public float WetlandThreshold;

    public float WetlandVegFloor;

    public float RiparianVegStrength;

    public uint RiparianBandPx;

}



internal static unsafe class BiomeParamsGpuMarshal

{

    internal const ulong UniformByteCount = 256;



    internal static void WriteUniform(nint mapped, in BiomeParamsGpu p)

    {

        Unsafe.InitBlock((void*)mapped, 0, (uint)UniformByteCount);

        *(BiomeParamsGpu*)(void*)mapped = p;

    }



    internal static BiomeParamsGpu Build(

        int width,

        int height,

        float snowLineMeters,

        in ErosionParams resolved,

        BiomapGenerationSettings biomapResolved,

        float flowAccumInvMax,

        uint biomeScatterNoiseSeed)

    {

        float cell = Math.Max(resolved.CellSizeMeters, 1f);

        float worley = Math.Clamp(36f + cell * 0.55f + biomapResolved.MoistureBlurSigmaMeters * 0.06f, 10f, 128f);

        uint riparianPx = (uint)Math.Clamp((int)Math.Ceiling(50.0 / cell), 1, 128);

        return new BiomeParamsGpu

        {

            Width = (uint)width,

            Height = (uint)height,

            SnowLineMeters = snowLineMeters,

            SeaLevelMeters = resolved.SeaLevelMeters,

            MaxElevationMeters = resolved.MaxElevationMeters,

            SnowSoftBandMeters = biomapResolved.SnowSoftBandMeters,

            CliffSlopeDegrees = biomapResolved.CliffSlopeDegrees,

            CliffSlopeSoftDegrees = biomapResolved.CliffSlopeSoftDegrees,

            VegWaterScale = biomapResolved.VegetationMoistureWeight * 4f,

            MoistDepthScale = 24f,

            TreeMoistureThreshold = 0.4f,

            TreeMaxSlopeDegrees = 30f,

            WorleyScalePx = worley,

            FlowAccumInvMax = flowAccumInvMax,

            NoiseSeed = biomeScatterNoiseSeed,

            WetlandThreshold = 0.01f,

            WetlandVegFloor = 0.85f,

            RiparianVegStrength = 0.68f,

            RiparianBandPx = riparianPx,

        };

    }

}


