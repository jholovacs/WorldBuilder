using System.Runtime.InteropServices;
/// <summary>Matches <c>RidgedNoise.hlsl</c> NoiseCb (std140).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NoiseParamsGpu
{
    public uint Width;
    public uint Height;
    public uint Seed;
    public uint Octaves;
    public float Lacunarity;
    public float Persistence;
    public float BaseFreq;
    public float Pad;
}

/// <summary>Matches <c>Hydraulic.hlsl</c> HydraulicCb.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct HydraulicParamsGpu
{
    public uint Width;
    public uint Height;
    public uint Seed;
    public uint PassIndex;
    public uint DropsPerPass;
    public uint Lifetime;
    public float HydraulicCapacityLimit;
    public float MaxSediment;
    public float Inertia;
    public float InvInertia;
    public float CapFactor;
    public float DepositRatio;
    public float ErodeRatio;
    public float Retention;
    public float Accel;
    public float MaxX;
    public float MaxY;
    public float MaxElevationMeters;
    public float CellSizeMeters;
    /// <summary>Gaussian σ for 3×3 sediment pile (<see cref="ErosionParams.HydraulicDepositDistributionSigmaPx"/>).</summary>
    public float HydraulicDepositGaussianSigmaPx;
    /// <summary>Weights droplet-carried water contributed to cumulative <see cref="Shaders"/> hydrology SSBO (<c>HydrologyPresenceGain</c>).</summary>
    public float HydrologyPresenceGain;
}

/// <summary>Matches shallow-water compute <c>ShallowWaterCb</c> (<c>ShallowWaterFlux.hlsl</c> / <c>ShallowWaterUpdate.hlsl</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ShallowWaterParamsGpu
{
    public uint Width;
    public uint Height;
    public float Dt;
    public float FluxK;
    public float VelocityDamping;
    public float Gravity;
    public float MinDepthEpsilon;
    public float MassOutFracClamp;
    public float FlowAccumMultiplier;
}
