using System.Runtime.CompilerServices;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

internal static class ShallowWaterGpu
{
    internal const ulong UniformByteCount = 256;

    /// <inheritdoc cref="ShallowWaterParamsGpu"/>
    internal static unsafe void WriteUniform(nint mapped, in ShallowWaterParamsGpu p)
    {
        Unsafe.InitBlock((void*)mapped, 0, (uint)UniformByteCount);
        *(ShallowWaterParamsGpu*)(void*)mapped = p;
    }

    /// <summary>Defaults tuned for normalized [0,1] terrain cells with ~dt mass exchange.</summary>
    internal static ShallowWaterParamsGpu BuildParams(int width, int height, in ErosionParams resolved)
    {
        _ = resolved;
        return new ShallowWaterParamsGpu
        {
            Width = (uint)width,
            Height = (uint)height,
            Dt = 0.08f,
            FluxK = 0.55f,
            VelocityDamping = 0.92f,
            Gravity = 0.35f,
            MinDepthEpsilon = 1e-5f,
            MassOutFracClamp = 0.22f,
            FlowAccumMultiplier = 2.5f,
        };
    }
}
