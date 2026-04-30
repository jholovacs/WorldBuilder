// dxc -spirv -HV 2021 -T cs_6_6 -E main -fvk-use-scalar-layout -fspv-target-env=vulkan1.3 -Fo ShallowWaterUpdate.spv ShallowWaterUpdate.hlsl

[[vk::binding(0)]]
StructuredBuffer<float> TerrainBed;

[[vk::binding(1)]]
StructuredBuffer<float4> WaterSrc;

[[vk::binding(2)]]
StructuredBuffer<float4> FluxIn;

[[vk::binding(3)]]
RWStructuredBuffer<float4> WaterDst;

[[vk::binding(4)]]
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

[numthreads(TG, TG, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint w = Width;
    uint h = Height;
    if (tid.x >= w || tid.y >= h)
        return;

    uint i = tid.y * w + tid.x;
    float zb = TerrainBed[i];

    float4 w0 = WaterSrc[i];

    float inW = (tid.x > 0u) ? FluxIn[i - 1u].x : 0.0f;
    float inS = (tid.y > 0u) ? FluxIn[i - w].y : 0.0f;
    float fe = FluxIn[i].x;
    float fn = FluxIn[i].y;

    float dh = inW + inS - fe - fn;
    float hx = max(w0.x + dh, MinDepthEpsilon);

    float zbW = zb;
    float zbE = zb;
    float zbS = zb;
    float zbN = zb;

    float hW = hx;
    float hE = hx;
    float hN = hx;
    float hS = hx;

    if (tid.x > 0u)
    {
        uint iw = tid.y * w + (tid.x - 1u);
        hW = max(WaterSrc[iw].x, MinDepthEpsilon);
        zbW = TerrainBed[iw];
    }

    if (tid.x + 1u < w)
    {
        uint ie = tid.y * w + (tid.x + 1u);
        hE = max(WaterSrc[ie].x, MinDepthEpsilon);
        zbE = TerrainBed[ie];
    }

    if (tid.y > 0u)
    {
        uint is = (tid.y - 1u) * w + tid.x;
        hS = max(WaterSrc[is].x, MinDepthEpsilon);
        zbS = TerrainBed[is];
    }

    if (tid.y + 1u < h)
    {
        uint in_ = (tid.y + 1u) * w + tid.x;
        hN = max(WaterSrc[in_].x, MinDepthEpsilon);
        zbN = TerrainBed[in_];
    }

    float etaC = zb + hx;
    float etaW = zbW + hW;
    float etaE = zbE + hE;
    float etaN = zbN + hN;
    float etaS = zbS + hS;

    float gradEtaX = 0.5f * (etaE - etaW);
    float gradEtaY = 0.5f * (etaN - etaS);

    float ux = w0.y * VelocityDamping - Gravity * Dt * gradEtaX;
    float uy = w0.z * VelocityDamping - Gravity * Dt * gradEtaY;
    ux = clamp(ux, -2.5f, 2.5f);
    uy = clamp(uy, -2.5f, 2.5f);

    float speed = sqrt(ux * ux + uy * uy);
    float flowStep = hx * speed * Dt;

    float wAccum = w0.w;
    wAccum += FlowAccumMultiplier * flowStep + FlowAccumMultiplier * (abs(inW - fe) + abs(inS - fn)) * Dt;

    bool onMapEdge = tid.x == 0u || tid.y == 0u || tid.x + 1u >= w || tid.y + 1u >= h;
    if (onMapEdge)
    {
        hx = 0.0f;
        ux = 0.0f;
        uy = 0.0f;
    }

    WaterDst[i] = float4(hx, ux, uy, wAccum);
}
