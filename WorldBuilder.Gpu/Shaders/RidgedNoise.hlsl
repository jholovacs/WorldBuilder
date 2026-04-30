// Ridged multifractal matching CPU TerrainNoise (normalized separately on CPU via max scan).
// Compiled: dxc -spirv -HV 2021 -T cs_6_0 -E main -fvk-use-scalar-layout -Fo RidgedNoise.spv RidgedNoise.hlsl

[[vk::binding(0)]]
RWStructuredBuffer<float> Heights;

[[vk::binding(1)]]
cbuffer NoiseCb : register(b0)
{
    uint Width;
    uint Height;
    uint Seed;
    uint Octaves;
    float Lacunarity;
    float Persistence;
    float BaseFreq;
    float Pad;
};

uint64_t SplitMix64(uint64_t z)
{
    z += 0x9E3779B97F4A7C15ull;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

float DotCornerGradient(int ix, int iy, float ox, float oy, uint64_t cornerSalt)
{
    uint64_t cell = (uint64_t)(((int64_t)ix * 73856093LL) ^ ((int64_t)iy * 19349663LL));
    uint64_t mixed = SplitMix64(cornerSalt ^ cell);
    float angle = ((mixed >> 40) / float(1 << 24)) * (6.28318530718);
    float gx = cos(angle);
    float gy = sin(angle);
    return gx * ox + gy * oy;
}

float Fade(float t)
{
    return t * t * t * (t * (t * 6.f - 15.f) + 10.f);
}

float Lerp(float a, float b, float t)
{
    return a + (b - a) * t;
}

float GradientNoise(float x, float y, uint seed, int octave)
{
    float xf = floor(x);
    float yf = floor(y);
    int xi = (int) xf;
    int yi = (int) yf;

    float tx = x - xf;
    float ty = y - yf;

    float u = Fade(tx);
    float v = Fade(ty);

    uint64_t salt = SplitMix64((uint64_t)(seed ^ (uint)(octave * 977)));
    float g00 = DotCornerGradient(xi, yi, tx, ty, salt ^ 0x9E3779B185EBCA87ull);
    float g10 = DotCornerGradient(xi + 1, yi, tx - 1.f, ty, salt ^ 0xC2B2AE3D27741FCDull);
    float g01 = DotCornerGradient(xi, yi + 1, tx, ty - 1.f, salt ^ 0x165667B19E3779F9ull);
    float g11 = DotCornerGradient(xi + 1, yi + 1, tx - 1.f, ty - 1.f, salt ^ 0xD3A2646C79C819DDull);

    float ix0 = Lerp(g00, g10, u);
    float ix1 = Lerp(g01, g11, u);
    return Lerp(ix0, ix1, v);
}

float RidgedSum(float nx, float ny)
{
    float lac = Lacunarity > 1e-6 ? Lacunarity : 2.f;
    float pers = Persistence > 1e-6 ? Persistence : 0.5f;
    int oct = (int) clamp((float)Octaves, 1.f, 16.f);
    float baseF = BaseFreq > 1e-6 ? BaseFreq : 4.f;

    float frequency = baseF;
    float amplitude = 0.5f;
    float weight = 1.f;
    float signalSum = 0.f;

    for (int o = 0; o < oct; o++)
    {
        float sx = nx * frequency * 8.f;
        float sy = ny * frequency * 8.f;

        float n = GradientNoise(sx, sy, Seed, o);
        n = abs(n);
        n = 1.f - n;
        n *= n;
        n *= weight;
        weight = max(n, 1e-6);

        signalSum += n * amplitude;

        frequency *= lac;
        amplitude *= pers;
    }

    return signalSum;
}

[numthreads(256, 1, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    uint flat = dtid.x;
    uint n = Width * Height;
    if (flat >= n)
        return;

    uint x = flat % Width;
    uint y = flat / Width;

    float nx = ((float)x + 0.5f) / (float) Width - 0.5f;
    float ny = ((float)y + 0.5f) / (float) Height - 0.5f;

    Heights[flat] = RidgedSum(nx, ny);
}
