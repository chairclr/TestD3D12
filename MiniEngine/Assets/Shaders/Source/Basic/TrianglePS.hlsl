#include "TriangleCommon.hlsl"

static const float3 LightDirection = normalize(float3(-1.4, -2.0, 2.6));

Texture2D<float2> ShadowTexture : register(t0);

float4 PSMain(PS_INPUT input) : SV_Target {

    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(-LightDirection);

    float diffuse = max(dot(normal, lightDir), 0.0);
    float3 litColor = diffuse + 0.1;

    float2 shadow = ShadowTexture.Load(int3(input.pos.xy, 0));
    bool occluded = shadow.x > 0;

    if (occluded) {
        //litColor = clamp(litColor - diffuse, 0.1, 1.0);
    }

    return float4(litColor, 1.0);
}
