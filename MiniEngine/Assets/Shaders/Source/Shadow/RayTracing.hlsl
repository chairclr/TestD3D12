RWTexture2D<float> ShadowMask : register(u0);

RaytracingAccelerationStructure SceneBVH : register(t0);
Texture2D<float> DepthTexture : register(t1);

static const float3 LightDirection = normalize(float3(-1.4, -2.0, 2.6));

// Constants
cbuffer constants : register(b0) {
    column_major float4x4 InverseViewProjection;
}

struct ShadowPayload {
    bool hit;
};

[shader("raygeneration")]
void RayGen()
{
    uint2 index = DispatchRaysIndex().xy;
    uint2 dim = DispatchRaysDimensions().xy;

    float depth = DepthTexture.Load(int3(index, 0));

    if (depth >= 1.0) {
        ShadowMask[index] = 0.0;
        return;
    }

    float2 uv = (index + 0.5) / dim;
    float2 ndc = uv * 2.0f - 1.0f;
    ndc.y = -ndc.y;

    float4 clipPos = float4(ndc, depth, 1.0f);
    float4 worldPosH = mul(InverseViewProjection, clipPos);
    worldPosH /= worldPosH.w;

    float3 origin = worldPosH.xyz;
    float3 direction = normalize(-LightDirection);

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.005;
    ray.TMax = 10000.0;

    ShadowPayload payload = { false };
    // Cool optimization here since we don't need any material info
    TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, ~0, 0, 1, 0, ray, payload);

    ShadowMask[index] = payload.hit ? 1.0 : 0.0;
}

[shader("closesthit")]
void ClosestHit(inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attr)
{
    payload.hit = true;
}

[shader("miss")]
void Miss(inout ShadowPayload payload)
{
    payload.hit = false;
}
