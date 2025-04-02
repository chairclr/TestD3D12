#include "FullscreenCommon.hlsl"

Texture2D<float> DepthTexture : register(t0);
SamplerState LinearSampler : register(s0);

float normalizeDepth(float depth) {
    return 0.01 / (1.01 - depth);
}

float4 PSMain(PS_INPUT input) : SV_Target {
    // We call normalizeDepth here to make it.. normal
    float depth = normalizeDepth(DepthTexture.Sample(LinearSampler, input.uv));

    return float4(depth.xxx, 1.0); // Convert to grayscale
}

