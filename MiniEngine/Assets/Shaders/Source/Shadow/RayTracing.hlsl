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
    float t;
};

float nrand(float2 uv, float s) {
    return frac(sin(dot(uv, float2(12.9898, 78.233))) * (43758.5453 * s * 2.0));
}

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
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    float4 clipPos = float4(ndc, depth, 1.0);
    float4 worldPosH = mul(InverseViewProjection, clipPos);
    worldPosH /= worldPosH.w;

    float3 origin = worldPosH.xyz;
    float3 direction = normalize(-LightDirection);
    float3 randomDir = normalize(float3(nrand(uv, depth), nrand(uv + float2(0.1, 0.2), depth), direction.z));

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = normalize(direction /*+ 0.01 * randomDir*/);
    ray.TMin = 0.005;
    ray.TMax = 10000.0;

    float acc = 0;
    ShadowPayload payload = { false, 0.0 };
    // Cool optimization here since we don't need any material info
    TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, ~0, 0, 1, 0, ray, payload);

    ShadowMask[index] = payload.hit ? 1.0 : 0.0;
}

[shader("closesthit")]
void ClosestHit(inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attr)
{
    payload.hit = true;
    payload.t = RayTCurrent();
}

[shader("miss")]
void Miss(inout ShadowPayload payload)
{
    payload.hit = false;
}
