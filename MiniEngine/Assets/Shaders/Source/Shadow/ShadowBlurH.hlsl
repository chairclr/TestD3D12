#include "ShadowCommon.hlsl"

RWTexture2D<float2> ShadowTexture : register(u0);
RWTexture2D<float> IntermedTexture : register(u1);

Texture2D<float> DepthTexture : register(t0);

float linearizeDepth(float depth, float nearZ, float farZ) {
    return nearZ * farZ / (farZ - depth * (farZ - nearZ));
}

[numthreads(32, 32, 1)]
void CSMainH(uint3 id : SV_DispatchThreadID) {
    float centerDepth = DepthTexture[id.xy];

    float dx = min(abs(DepthTexture[id.xy - uint2(1, 0)] - centerDepth), abs(DepthTexture[id.xy + uint2(1, 0)] - centerDepth));
    float dy = min(abs(DepthTexture[id.xy - uint2(0, 1)] - centerDepth), abs(DepthTexture[id.xy + uint2(0, 1)] - centerDepth));

    bool inShadow = ShadowTexture[id.xy].x > 0.0;

    float blurRadius;

    if (inShadow) {
        blurRadius = lerp(0.1, 16.0, saturate((ShadowTexture[id.xy].y) / 4.0));
    }
    else {
        float maxOccluderDepth = 0.0;
        for (int i = -16; i <= 16; i++) {
            maxOccluderDepth = max(maxOccluderDepth, max(ShadowTexture[id.xy + int2(i, 0)].y, ShadowTexture[id.xy + int2(0, i)].y));
        }

        blurRadius = lerp(0.1, 16.0, saturate((maxOccluderDepth) / 4.0));
    }

    blurRadius = clamp(blurRadius / linearizeDepth(centerDepth, 0.05, 1000.0), 0, 16.0);

    int radius = int(ceil(blurRadius));

    // Actual box blur pass
    float sum = 0.0f;
    float weight = 0.0f;

    for (int y = -radius; y <= radius; y++) {
        for (int x = -radius; x <= radius; x++) {
            if (length(float2(x, y)) > blurRadius + 0.5)
                continue;

            int2 offset = int2(x, y);
            uint2 samplePos = id.xy + offset;

            float sampleDepth = DepthTexture[samplePos];
            float sampleShadow = ShadowTexture[samplePos].x;

            float dxAbs = abs(float(x));
            float dyAbs = abs(float(y));

            if (abs(centerDepth - sampleDepth) > dxAbs * dx + dyAbs * dy + BLUR_DEPTH_EPSILON)
                continue;

            sum += sampleShadow;
            weight += 1.0f;
        }
    }

    IntermedTexture[id.xy].x = (weight > 0.0f) ? sum / weight : 0.0f;
}
