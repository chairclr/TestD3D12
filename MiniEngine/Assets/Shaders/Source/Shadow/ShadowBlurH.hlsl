RWTexture2D<float2> ShadowTexture : register(u0);
RWTexture2D<float> IntermedTexture : register(u1);

Texture2D<float> DepthTexture : register(t0);

[numthreads(16, 16, 1)]
void CSMainH(uint3 id : SV_DispatchThreadID) {
    float centerDepth = DepthTexture[id.xy];

    float dx = min(abs(DepthTexture[id.xy - uint2(1, 0)] - centerDepth), abs(DepthTexture[id.xy + uint2(1, 0)] - centerDepth));
    float dy = min(abs(DepthTexture[id.xy - uint2(0, 1)] - centerDepth), abs(DepthTexture[id.xy + uint2(0, 1)] - centerDepth));

    bool inShadow = ShadowTexture[id.xy].x > 0.0;

    float blurRadius;

    if (inShadow) {
        blurRadius = lerp(0.0, 64.0, saturate((ShadowTexture[id.xy].y) / 16.0));
    }
    else {
        float maxOccluderDepth = 0.0;
        for (int y = -8; y <= 8; y++) {
            for (int x = -8; x <= 8; x++) {
                int2 offset = int2(x, y);
                uint2 samplePos = id.xy + offset;

                float sampleDepth = DepthTexture[samplePos];
                float sampleShadow = ShadowTexture[samplePos].x;

                float dxAbs = abs(float(x));
                float dyAbs = abs(float(y));

                if (abs(centerDepth - sampleDepth) > dxAbs * dx + dyAbs * dy + 1e-5)
                    continue;

                maxOccluderDepth = max(maxOccluderDepth, ShadowTexture[samplePos].y);
            }
        }

        blurRadius = lerp(0.0, 64.0, saturate((maxOccluderDepth) / 16.0));
    }

    int radius = int(ceil(blurRadius));

    // Actual box blur pass
    float sum = 0.0f;
    float weight = 0.0f;

    for (int y = -radius; y <= radius; y++) {
        for (int x = -radius; x <= radius; x++) {
            int2 offset = int2(x, y);
            uint2 samplePos = id.xy + offset;

            float sampleDepth = DepthTexture[samplePos];
            float sampleShadow = ShadowTexture[samplePos].x;

            float dxAbs = abs(float(x));
            float dyAbs = abs(float(y));

            if (abs(centerDepth - sampleDepth) > dxAbs * dx + dyAbs * dy + 0.001)
                continue;

            sum += sampleShadow;
            weight += 1.0f;
        }
    }

    IntermedTexture[id.xy].x = blurRadius / 64.0;
}
