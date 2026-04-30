// dxc -spirv -HV 2021 -T cs_6_6 -E CalculateBiomes -fvk-use-scalar-layout -fspv-target-env=vulkan1.3 -Fo CalculateBiomes.spv CalculateBiomes.hlsl

//

// Per-texel grid (row-major index = y * Width + x), same addressing as a 2D texture.

// R = snow, G = vegetation (hydraulic/shallow waterMap: wetlands, riparian band near surface water, residual depth), B = rock/cliff, A = tree density (wet + slope + snow-line gates, noise × Worley clumping).

// Integrated flow (binding 4) drives moisture with depth so channels are not a uniform sheet.



[[vk::binding(0)]]

StructuredBuffer<float> heightMap;



[[vk::binding(1)]]

StructuredBuffer<float> waterMap;



[[vk::binding(2)]]

StructuredBuffer<float> slopeMap;



[[vk::binding(3)]]

RWStructuredBuffer<float4> biomeMap;



[[vk::binding(4)]]

StructuredBuffer<float> flowAccumMap;



cbuffer BiomeCb : register(b0)

{

    uint Width;

    uint Height;

    float SnowLineMeters;

    float SeaLevelMeters;



    float MaxElevationMeters;

    float SnowSoftBandMeters;

    float CliffSlopeDegrees;

    float CliffSlopeSoftDegrees;



    float VegWaterScale;

    float MoistDepthScale;

    float TreeMoistureThreshold;

    float TreeMaxSlopeDegrees;



    float WorleyScalePx;

    float FlowAccumInvMax;

    uint NoiseSeed;

    float WetlandThreshold;

    float WetlandVegFloor;

    float RiparianVegStrength;

    uint RiparianBandPx;

};



static const uint TG = 8u;



float SmoothSat(float e0, float e1, float x)

{

    if (x <= e0)

        return 0.0f;

    if (x >= e1)

        return 1.0f;

    float t = (x - e0) / (e1 - e0);

    return t * t * (3.0f - 2.0f * t);

}



uint MurmurFinalize(uint z)

{

    z ^= z >> 16;

    z *= 0x7feb352du;

    z ^= z >> 15;

    z *= 0x846ca68bu;

    z ^= z >> 16;

    return z;

}



uint HashCell(uint x, uint y, uint seed)

{

    uint h = x * 374761393u + y * 668265263u;

    return MurmurFinalize(h ^ seed);

}



float WhiteNoise01(uint2 tid, uint layer)

{

    uint h = HashCell(tid.x, tid.y, NoiseSeed ^ (layer * 2654435761u));

    return float(h & 0xFFFFFFu) * (1.0f / 16777216.0f);

}



// First-neighbour Worley distance in texel space (smaller distance → ridge line / clump core).

float WorleyF1(float2 pTex, uint seed)

{

    float2 ip = floor(pTex);

    float md = 1e10;

    [unroll] for (int j = -2; j <= 2; j++)

    {

        [unroll] for (int i = -2; i <= 2; i++)

        {

            int2 cell = int2(ip) + int2(i, j);

            uint h0 = HashCell(uint(cell.x), uint(cell.y), seed);

            uint h1 = HashCell(uint(cell.y), uint(cell.x), seed ^ 0xCAFEBABEu);

            float2 feat = float2(cell) +

                          float2(float(h0 & 0xFFFFu), float(h1 & 0xFFFFu)) * (1.0f / 65536.0f);

            float d = distance(pTex, feat);

            md = min(md, d);

        }

    }

    return md;

}



[numthreads(TG, TG, 1)]

void CalculateBiomes(uint3 tid : SV_DispatchThreadID)

{

    uint w = Width;

    uint h = Height;

    if (tid.x >= w || tid.y >= h)

        return;



    uint i = tid.y * w + tid.x;



    float hn = heightMap[i];

    float hMeters = hn * MaxElevationMeters;



    float edgeLo = SnowLineMeters - SnowSoftBandMeters;

    float edgeHi = SnowLineMeters + SnowSoftBandMeters;

    float snowF01 = SmoothSat(edgeLo, edgeHi, hMeters);



    float waterDepth = max(waterMap[i], 0.0f);

    float slopeDeg = slopeMap[i];

    bool isWetland = waterDepth > WetlandThreshold;

    bool nearWetland = false;

    uint rRip = RiparianBandPx;

    if (!isWetland && rRip > 0u)

    {

        int xi = int(tid.x);

        int yi = int(tid.y);

        int iR = int(rRip);

        for (int dy = -iR; dy <= iR; dy++)

        {

            if (nearWetland)

                break;

            int yy = yi + dy;

            if (yy < 0 || yy >= int(h))

                continue;

            for (int dx = -iR; dx <= iR; dx++)

            {

                int xx = xi + dx;

                if (xx < 0 || xx >= int(w))

                    continue;

                float nw = waterMap[uint(yy) * w + uint(xx)];

                if (nw > WetlandThreshold)

                    nearWetland = true;

            }

        }

    }

    float cliffDeg = CliffSlopeDegrees;

    float cliffSoft = max(CliffSlopeSoftDegrees, 0.25f);

    float rockF01 = SmoothSat(cliffDeg - cliffSoft * 0.5f, cliffDeg + cliffSoft * 1.5f, slopeDeg);

    float vegFromHydrology = saturate(waterDepth * VegWaterScale);

    if (isWetland)

        vegFromHydrology = WetlandVegFloor;

    else if (nearWetland)

        vegFromHydrology = max(vegFromHydrology, RiparianVegStrength);

    float vegRaw = vegFromHydrology;

    vegRaw *= saturate(1.0f - rockF01);

    bool wet = hMeters < SeaLevelMeters;

    vegRaw = max(vegRaw, wet ? 0.35f : 0.0f);



    float snowF = snowF01;

    float vegF = saturate(vegRaw);

    float rockF = rockF01;



    float fac = saturate(flowAccumMap[i] * FlowAccumInvMax);

    float depthMoist = saturate(waterDepth * MoistDepthScale);

    float moisture = saturate(max(depthMoist, sqrt(fac)));



    if (wet)

        moisture = max(moisture, 0.92f);



    bool treeEligible =

        (slopeDeg < TreeMaxSlopeDegrees) &&

        (hMeters < SnowLineMeters) &&

        (moisture > TreeMoistureThreshold);



    float treeBase = 0.0f;

    if (treeEligible)

    {

        float mSpan = saturate((moisture - TreeMoistureThreshold) / max(1e-4f, 1.0f - TreeMoistureThreshold));

        float slopeRel = saturate(1.0f - slopeDeg / max(TreeMaxSlopeDegrees, 1e-4f));

        treeBase = saturate(mSpan * slopeRel);

    }



    float wn = WhiteNoise01(tid.xy, 1u);

    float scaler = WorleyScalePx <= 1e-4f ? 12.0f : WorleyScalePx;

    float2 pW = float2(float(tid.x), float(tid.y)) / scaler;

    float wd = WorleyF1(pW, NoiseSeed ^ 0x5F3759DFu);



    float clump =

        saturate(1.0f - smoothstep(0.08f * scaler, 0.55f * scaler, wd));



    float treeDensity =

        saturate(treeBase * (0.38f + 0.62f * wn) * (0.28f + 0.72f * clump));



    biomeMap[i] = float4(snowF, vegF, rockF, treeDensity);

}

