#include "TriangleCommon.hlsl"

Texture2D<float2> ShadowTexture : register(t0);
SamplerState ShadowSampler : register(s0);

cbuffer constants : register(b0) {
    float2 WindowSize;
    float2 __0;
    float3 LightPosition;
    float __1;
    float4x2 __2;
}

float4 PSMain(PS_INPUT input) : SV_Target {

    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(LightPosition - input.world_pos.xyz);

    float2 shadow = ShadowTexture.Sample(ShadowSampler, input.pos.xy / WindowSize);

    float ambient = 0.1;
    float diffuse = max(dot(normal, lightDir), 0.0);
    float shadowFactor = saturate(1.0 - shadow.x);

    float3 color = ambient + diffuse * shadowFactor;

    return float4(color, 1.0);
}
