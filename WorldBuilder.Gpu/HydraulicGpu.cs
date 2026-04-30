using System.Runtime.CompilerServices;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

internal static class HydraulicGpu
{
    /// <summary>Must match <c>kFixed</c> in Hydraulic.hlsl.</summary>
    public const float FixedScale = 65536f;

    internal const ulong UniformByteCount = 256;

    internal static HydraulicParamsGpu BuildParams(
        int width,
        int height,
        uint seed,
        uint passIndex,
        uint dropsPerPass,
        in ErosionParams p)
    {
        int lifetime = Math.Clamp(p.DropletMaxLifetime, 4, 512);
        float inertia = Math.Clamp(p.DropletInertia, 0f, 1f);
        float invInertia = 1f - inertia;
        float capFactor = Math.Max(1e-6f, p.SedimentCapacityFactor);
        float hydraulicCapLimit = p.HydraulicCapacityLimit > 0f ? Math.Clamp(p.HydraulicCapacityLimit, 0f, 1f) : 0f;
        float maxSediment = p.MaxSediment > 0f ? p.MaxSediment : 0f;
        float depositRatio = Math.Clamp(p.DepositRatio, 0f, 1f);
        float erodeRatio = Math.Clamp(p.ErodeRatio, 0f, 1f);
        float retention = Math.Clamp(p.WaterRetention, 0f, 1f);
        float accel = Math.Max(0f, p.DropletAcceleration);
        float maxX = width - 1.000001f;
        float maxY = height - 1.000001f;
        float maxElev = Math.Max(p.MaxElevationMeters, 1f);
        float cellSize = Math.Max(p.CellSizeMeters, 1e-6f);
        float hydDepSigma =
            Math.Clamp(p.HydraulicDepositDistributionSigmaPx <= 0f ? 0.55f : p.HydraulicDepositDistributionSigmaPx, 0.08f, 2f);

        return new HydraulicParamsGpu
        {
            Width = (uint)width,
            Height = (uint)height,
            Seed = seed,
            PassIndex = passIndex,
            DropsPerPass = dropsPerPass,
            Lifetime = (uint)lifetime,
            HydraulicCapacityLimit = hydraulicCapLimit,
            MaxSediment = maxSediment,
            Inertia = inertia,
            InvInertia = invInertia,
            CapFactor = capFactor,
            DepositRatio = depositRatio,
            ErodeRatio = erodeRatio,
            Retention = retention,
            Accel = accel,
            MaxX = maxX,
            MaxY = maxY,
            MaxElevationMeters = maxElev,
            CellSizeMeters = cellSize,
            HydraulicDepositGaussianSigmaPx = hydDepSigma,
            HydrologyPresenceGain = 0.045f,
        };
    }

    internal static unsafe void WriteUniform(nint mapped, in HydraulicParamsGpu p)
    {
        Unsafe.InitBlock((void*)mapped, 0, (uint)UniformByteCount);
        *(HydraulicParamsGpu*)(void*)mapped = p;
    }
}
