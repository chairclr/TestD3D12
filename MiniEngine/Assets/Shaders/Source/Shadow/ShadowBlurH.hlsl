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
        blurRadius = lerp(1.0, 64.0, saturate((ShadowTexture[id.xy].y) / 16.0));
    }
    else {
        float maxOccluderDist = 0.0;
        for (int i = -16; i < 16; i++) {
            uint2 px = uint2(id.x + i, id.y);
            uint2 py = uint2(id.x, id.y + i);

            float occluderDistX = ShadowTexture[px].y;
            float occluderDistY = ShadowTexture[py].y;

            maxOccluderDist = max(maxOccluderDist, max(occluderDistX, occluderDistY));
        }

        blurRadius = lerp(1.0, 64.0, saturate((maxOccluderDist) / 16.0));
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

            if (abs(centerDepth - sampleDepth) > dxAbs * dx + dyAbs * dy + 0.001f)
                continue;

            sum += sampleShadow;
            weight += 1.0f;
        }
    }

    IntermedTexture[id.xy].x = (weight > 0.0f) ? sum / weight : 0.0f;
}
