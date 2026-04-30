// Single-thread-group droplet: one invocation per hydraulic drop (lifetime loop in-shader).
// Fixed-point int atomics on height/deposit/flow buffers (SPIR-V OpAtomicIAdd).
// Compile: dxc -spirv -HV 2021 -T cs_6_6 -E main -fvk-use-scalar-layout -fspv-target-env=vulkan1.3 -Fo Hydraulic.spv Hydraulic.hlsl

static const float kFixed = 65536.0;

[[vk::binding(0)]]
StructuredBuffer<float> Snapshot;

[[vk::binding(1)]]
globallycoherent RWStructuredBuffer<int> HeightDeltaI;

[[vk::binding(2)]]
globallycoherent RWStructuredBuffer<int> DepositDeltaI;

[[vk::binding(3)]]
globallycoherent RWStructuredBuffer<int> FlowDeltaI;

/// Cumulative hydraulic water-presence splats (persist across passes until CPU reset — normalized for biomap / shallow-water bridge).
[[vk::binding(4)]]
globallycoherent RWStructuredBuffer<int> WaterHydrologyI;

[[vk::binding(5)]]
cbuffer HydraulicCb : register(b0)
{
    uint Width;
    uint Height;
    uint Seed;
    uint PassIndex;
    uint DropsPerPass;
    uint Lifetime;
    float HydraulicCapacityLimit;
    float MaxSediment;
    float Inertia;
    float InvInertia;
    float CapFactor;
    float DepositRatio;
    float ErodeRatio;
    float Retention;
    float Accel;
    float MaxX;
    float MaxY;
    float MaxElevationMeters;
    float CellSizeMeters;
    float HydraulicDepositGaussianSigmaPx;
    float HydrologyPresenceGain;
}

uint64_t SplitMix64(uint64_t z)
{
    z += 0x9E3779B97F4A7C15ull;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

float NextFloat01(inout uint64_t state)
{
    state = SplitMix64(state);
    return (float)(state >> 40) / (float)(1 << 24);
}

float SampleBilinear(int w, int h, float fx, float fy)
{
    fx = clamp(fx, 0.0f, (float)(w - 1));
    fy = clamp(fy, 0.0f, (float)(h - 1));

    int x0 = (int)floor(fx);
    int y0 = (int)floor(fy);
    int x1 = min(x0 + 1, w - 1);
    int y1 = min(y0 + 1, h - 1);

    float tx = fx - (float)x0;
    float ty = fy - (float)y0;

    float h00 = Snapshot[y0 * w + x0];
    float h10 = Snapshot[y0 * w + x1];
    float h01 = Snapshot[y1 * w + x0];
    float h11 = Snapshot[y1 * w + x1];

    float ix0 = h00 + (h10 - h00) * tx;
    float ix1 = h01 + (h11 - h01) * tx;
    return ix0 + (ix1 - ix0) * ty;
}

void AtomicAddInt(globallycoherent RWStructuredBuffer<int> buf, uint idx, int v)
{
    if (v == 0)
        return;
    int unused;
    InterlockedAdd(buf[idx], v, unused);
}

/// Fixed-point bilinear splat: integer atomics sum exactly to round(delta * kFixed) (no per-corner round drift).
void AtomicAddFixedBilinear4(
    globallycoherent RWStructuredBuffer<int> buf,
    uint i00,
    uint i10,
    uint i01,
    uint i11,
    float delta,
    float w00,
    float w10,
    float w01,
    float w11)
{
    float scaled = delta * kFixed;
    int total = (int)round(scaled);
    if (total == 0)
        return;

    float ex00 = scaled * w00;
    float ex10 = scaled * w10;
    float ex01 = scaled * w01;
    float ex11 = scaled * w11;

    int q00 = (int)floor(ex00);
    int q10 = (int)floor(ex10);
    int q01 = (int)floor(ex01);
    int q11 = (int)floor(ex11);

    int rem = total - q00 - q10 - q01 - q11;

    [unroll]
    for (int it = 0; it < 8; it++)
    {
        if (rem == 0)
            break;

        if (rem > 0)
        {
            float f0 = ex00 - (float)q00;
            float f1 = ex10 - (float)q10;
            float f2 = ex01 - (float)q01;
            float f3 = ex11 - (float)q11;
            if (f0 >= f1 && f0 >= f2 && f0 >= f3)
            {
                q00++;
                rem--;
            }
            else if (f1 >= f2 && f1 >= f3)
            {
                q10++;
                rem--;
            }
            else if (f2 >= f3)
            {
                q01++;
                rem--;
            }
            else
            {
                q11++;
                rem--;
            }
        }
        else
        {
            float f0 = ex00 - (float)q00;
            float f1 = ex10 - (float)q10;
            float f2 = ex01 - (float)q01;
            float f3 = ex11 - (float)q11;
            if (f0 <= f1 && f0 <= f2 && f0 <= f3)
            {
                q00--;
                rem++;
            }
            else if (f1 <= f2 && f1 <= f3)
            {
                q10--;
                rem++;
            }
            else if (f2 <= f3)
            {
                q01--;
                rem++;
            }
            else
            {
                q11--;
                rem++;
            }
        }
    }

    AtomicAddInt(buf, i00, q00);
    AtomicAddInt(buf, i10, q10);
    AtomicAddInt(buf, i01, q01);
    AtomicAddInt(buf, i11, q11);
}

void AtomicAddFixedWeighted9(
    globallycoherent RWStructuredBuffer<int> buf,
    float delta,
    float wn[9],
    uint idx[9])
{
    float scaled = delta * kFixed;
    int total = (int)round(scaled);
    if (total == 0)
        return;

    float ex[9];
    [unroll]
    for (uint k = 0; k < 9; k++)
        ex[k] = scaled * wn[k];

    int q[9];
    [unroll]
    for (uint k = 0; k < 9; k++)
        q[k] = (int)floor(ex[k]);

    int sq = q[0] + q[1] + q[2] + q[3] + q[4] + q[5] + q[6] + q[7] + q[8];
    int rem = total - sq;

    [loop]
    for (uint it = 0; it < 40 && rem != 0; it++)
    {
        if (rem > 0)
        {
            uint bi = 0;
            float bf = ex[0] - (float)q[0];
            [unroll]
            for (uint j = 1; j < 9; j++)
            {
                float f = ex[j] - (float)q[j];
                if (f > bf)
                {
                    bf = f;
                    bi = j;
                }
            }
            q[bi]++;
            rem--;
        }
        else
        {
            uint bi = 0;
            float bf = ex[0] - (float)q[0];
            [unroll]
            for (uint j = 1; j < 9; j++)
            {
                float f = ex[j] - (float)q[j];
                if (f < bf)
                {
                    bf = f;
                    bi = j;
                }
            }
            q[bi]--;
            rem++;
        }
    }

    AtomicAddInt(buf, idx[0], q[0]);
    AtomicAddInt(buf, idx[1], q[1]);
    AtomicAddInt(buf, idx[2], q[2]);
    AtomicAddInt(buf, idx[3], q[3]);
    AtomicAddInt(buf, idx[4], q[4]);
    AtomicAddInt(buf, idx[5], q[5]);
    AtomicAddInt(buf, idx[6], q[6]);
    AtomicAddInt(buf, idx[7], q[7]);
    AtomicAddInt(buf, idx[8], q[8]);
}

void AddGaussianHydraulicDepositPile(float fx, float fy, float depositAmt, int w, int h)
{
    if (depositAmt <= 0.0f)
        return;

    fx = clamp(fx, 0.0f, (float)(w - 1));
    fy = clamp(fy, 0.0f, (float)(h - 1));

    float sigma = max(HydraulicDepositGaussianSigmaPx, 8e-2f);
    float twoSigmaSq = 2.0f * sigma * sigma;

    int cx = clamp((int)floor(fx + 0.5f), 1, w - 2);
    int cy = clamp((int)floor(fy + 0.5f), 1, h - 2);

    float wn[9];
    uint ix[9];
    float sumW = 0.0f;
    uint kk = 0;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            int sx = cx + dx;
            int sy = cy + dy;
            float lx = fx - (float)sx;
            float ly = fy - (float)sy;
            float dist2 = lx * lx + ly * ly;
            float g = exp(-dist2 / twoSigmaSq);
            wn[kk] = g;
            sumW += g;
            ix[kk] = (uint)(sy * w + sx);
            kk++;
        }
    }

    float invSumW = rcp(sumW);
    [unroll]
    for (uint u = 0; u < 9; u++)
        wn[u] *= invSumW;

    AtomicAddFixedWeighted9(HeightDeltaI, depositAmt, wn, ix);
    AtomicAddFixedWeighted9(DepositDeltaI, depositAmt, wn, ix);
}

void AddBilinearHeight(float fx, float fy, float delta, int w, int h)
{
    fx = clamp(fx, 0.0f, (float)(w - 1));
    fy = clamp(fy, 0.0f, (float)(h - 1));

    int x0 = (int)floor(fx);
    int y0 = (int)floor(fy);
    int x1 = min(x0 + 1, w - 1);
    int y1 = min(y0 + 1, h - 1);

    float tx = fx - (float)x0;
    float ty = fy - (float)y0;

    float bw00 = (1.0f - tx) * (1.0f - ty);
    float bw10 = tx * (1.0f - ty);
    float bw01 = (1.0f - tx) * ty;
    float bw11 = tx * ty;

    uint i00 = (uint)(y0 * w + x0);
    uint i10 = (uint)(y0 * w + x1);
    uint i01 = (uint)(y1 * w + x0);
    uint i11 = (uint)(y1 * w + x1);

    AtomicAddFixedBilinear4(HeightDeltaI, i00, i10, i01, i11, delta, bw00, bw10, bw01, bw11);
}

void AddBilinearWeighted(globallycoherent RWStructuredBuffer<int> buf, float fx, float fy, float amount, int w, int h)
{
    if (amount <= 0.0f)
        return;

    fx = clamp(fx, 0.0f, (float)(w - 1));
    fy = clamp(fy, 0.0f, (float)(h - 1));

    int x0 = (int)floor(fx);
    int y0 = (int)floor(fy);
    int x1 = min(x0 + 1, w - 1);
    int y1 = min(y0 + 1, h - 1);

    float tx = fx - (float)x0;
    float ty = fy - (float)y0;

    float bw00 = (1.0f - tx) * (1.0f - ty);
    float bw10 = tx * (1.0f - ty);
    float bw01 = (1.0f - tx) * ty;
    float bw11 = tx * ty;

    uint i00 = (uint)(y0 * w + x0);
    uint i10 = (uint)(y0 * w + x1);
    uint i01 = (uint)(y1 * w + x0);
    uint i11 = (uint)(y1 * w + x1);

    AtomicAddFixedBilinear4(buf, i00, i10, i01, i11, amount, bw00, bw10, bw01, bw11);
}

[numthreads(256, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint3 tid : SV_GroupThreadID)
{
    uint dropletId = tid.x + groupId.x * 256u;
    if (dropletId >= DropsPerPass)
        return;

    int w = (int)Width;
    int h = (int)Height;
    uint pass = PassIndex;

    uint64_t rng = SplitMix64((uint64_t)Seed ^ 0xC001D00DCAFEull
        ^ (((uint64_t)pass << 32) | (uint64_t)dropletId));

    float fx = NextFloat01(rng) * MaxX;
    float fy = NextFloat01(rng) * MaxY;

    float vx = 0.0f;
    float vy = 0.0f;
    float sediment = 0.0f;
    float water = 1.0f;

    int life = (int)Lifetime;

    for (int step = 0; step < life && water > 1e-4f; step++)
    {
        fx = clamp(fx, 0.0f, MaxX);
        fy = clamp(fy, 0.0f, MaxY);

        int xi = (int)floor(fx);
        int yi = (int)floor(fy);
        if (xi <= 1 || xi >= w - 2 || yi <= 1 || yi >= h - 2)
            break;

        AddBilinearWeighted(FlowDeltaI, fx, fy, water, w, h);
        AddBilinearWeighted(WaterHydrologyI, fx, fy, water * HydrologyPresenceGain, w, h);

        float gx = (SampleBilinear(w, h, fx + 1.0f, fy) - SampleBilinear(w, h, fx - 1.0f, fy)) * 0.5f;
        float gy = (SampleBilinear(w, h, fx, fy + 1.0f) - SampleBilinear(w, h, fx, fy - 1.0f)) * 0.5f;

        float nx = -gx * Accel * InvInertia * water;
        float ny = -gy * Accel * InvInertia * water;
        vx = Inertia * vx + nx;
        vy = Inertia * vy + ny;

        const float maxVel = 3.0f;
        float vmag = sqrt(vx * vx + vy * vy);
        if (vmag > maxVel && vmag > 1e-6f)
        {
            float s = maxVel / vmag;
            vx *= s;
            vy *= s;
            vmag = maxVel;
        }

        float slopeMag = sqrt(gx * gx + gy * gy);
        float capacity = max(0.0f, vmag * slopeMag * CapFactor);
        if (MaxSediment > 0.0f)
            capacity = min(capacity, MaxSediment);

        const float slopeRadFive = 5.0 * 3.14159265 / 180.0;
        float tanSlopeThreshold = tan(slopeRadFive);
        float tanSlopeApprox = slopeMag * MaxElevationMeters / max(CellSizeMeters, 1e-6);
        float gentleSlopeBlend = tanSlopeApprox < tanSlopeThreshold ? (1.0 - tanSlopeApprox / tanSlopeThreshold) : 0.0;
        const float lowVelRef = 1.0f;
        float lowVelBlend = vmag < lowVelRef ? (1.0 - vmag / lowVelRef) : 0.0;
        float boostBlend = max(gentleSlopeBlend, lowVelBlend);
        float depositBoost = 1.0 + 2.0 * boostBlend;
        float effectiveDepositRatio = clamp(DepositRatio * depositBoost, 0.0, 1.0);

        if (sediment > capacity)
        {
            float depositAmt = effectiveDepositRatio * (sediment - capacity);
            sediment -= depositAmt;
            AddGaussianHydraulicDepositPile(fx, fy, depositAmt, w, h);
        }
        else
        {
            float deficit = capacity - sediment;
            float erodeAmt = ErodeRatio * deficit;
            float hLocal = SampleBilinear(w, h, fx, fy);
            float maxByHeight = (HydraulicCapacityLimit > 0.0f)
                ? HydraulicCapacityLimit * hLocal
                : 3.402823466e38f;
            erodeAmt = min(erodeAmt, maxByHeight);
            AddBilinearHeight(fx, fy, -erodeAmt, w, h);
            sediment += erodeAmt;
        }

        water *= Retention;

        fx += vx;
        fy += vy;

        if (fx < 0.0f || fy < 0.0f || fx > MaxX || fy > MaxY)
            break;
    }
}
