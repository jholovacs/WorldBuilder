// dxc -spirv -HV 2021 -T cs_6_6 -E main -fvk-use-scalar-layout -fspv-target-env=vulkan1.3 -Fo ShallowWaterFlux.spv ShallowWaterFlux.hlsl

[[vk::binding(0)]]
StructuredBuffer<float> TerrainBed;

[[vk::binding(1)]]
StructuredBuffer<float4> WaterRead;

[[vk::binding(2)]]
RWStructuredBuffer<float4> FluxOut;

[[vk::binding(3)]]
cbuffer ShallowWaterCb : register(b0)
{
    uint Width;
    uint Height;
    float Dt;
    float FluxK;
    float VelocityDamping;
    float Gravity;
    float MinDepthEpsilon;
    float MassOutFracClamp;
    float FlowAccumMultiplier;
};

static const uint TG = 8u;

// Conductivity for inter-cell flux; avoids clamping dried cells up to epsilon (pairs with edge sinks).
float FluxConductivity(float a, float b, float eps)
{
    float s = max(a + b, eps);
    return (a * b) / s;
}

[numthreads(TG, TG, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint w = Width;
    uint h = Height;
    if (tid.x >= w || tid.y >= h)
        return;

    uint i = tid.y * w + tid.x;
    float zb = TerrainBed[i];

    float4 wr = WaterRead[i];
    float hi = max(wr.x, 0.0f);
    float etaI = zb + hi;

    float fluxE = 0.0f;
    if (tid.x < w - 1)
    {
        uint ie = tid.y * w + (tid.x + 1);
        float zbe = TerrainBed[ie];
        float he = max(WaterRead[ie].x, 0.0f);
        float etaE = zbe + he;
        float dHead = etaI - etaE;
        float hm = FluxConductivity(hi, he, MinDepthEpsilon);
        float dq = FluxK * dHead * hm * Dt;
        if (dq > 0.0f)
            fluxE = min(dq, hi * MassOutFracClamp);
    }

    float fluxN = 0.0f;
    if (tid.y < h - 1)
    {
        uint in_ = (tid.y + 1u) * w + tid.x;
        float zbn = TerrainBed[in_];
        float hn = max(WaterRead[in_].x, 0.0f);
        float etaN = zbn + hn;
        float dHead = etaI - etaN;
        float hm = FluxConductivity(hi, hn, MinDepthEpsilon);
        float dq = FluxK * dHead * hm * Dt;
        if (dq > 0.0f)
            fluxN = min(dq, hi * MassOutFracClamp);
    }

    FluxOut[i] = float4(fluxE, fluxN, 0.0f, 0.0f);
}
