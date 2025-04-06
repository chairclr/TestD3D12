#include "TriangleCommon.hlsl"

static const float3 LightDirection = normalize(float3(-1.4, -2.0, 2.6));

Texture2D<float> ShadowTexture : register(t0);

float4 PSMain(PS_INPUT input) : SV_Target {

    float3 normal = normalize(input.normal);
    float3 lightDir = normalize(-LightDirection);

    float diffuse = max(dot(normal, lightDir), 0.0);

    float3 litColor = diffuse + 0.1.xxx;

    if (ShadowTexture.Load(int3(input.pos.xy, 0)) > 0.5)
    {
        litColor = 0.1.xxx;
    }

    return float4(litColor, 1.0);
}
