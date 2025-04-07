RWTexture2D<float2> ShadowMask : register(u0);

RaytracingAccelerationStructure SceneBVH : register(t0);
Texture2D<float> DepthTexture : register(t1);

static const float3 LightPosition = float3(0.1, 1.4, 4.2);

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

float4 depthToWorld(float2 uv, float depth) {
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    float4 clipPos = float4(ndc, depth, 1.0);
    float4 worldPosH = mul(InverseViewProjection, clipPos);
    worldPosH /= worldPosH.w;

    return worldPosH;
}

float3 depthToNormal(uint2 index, float2 uv, float depth, float2 texelSize) {
    float depthLeft = DepthTexture.Load(int3(index - int2(1, 0), 0));
    float depthRight = DepthTexture.Load(int3(index + int2(1, 0), 0));
    float depthTop = DepthTexture.Load(int3(index - int2(0, 1), 0));
    float depthBottom = DepthTexture.Load(int3(index + int2(0, 1), 0));

    float4 posLeft = depthToWorld(uv - float2(texelSize.x, 0), depthLeft);
    float4 posRight = depthToWorld(uv + float2(texelSize.x, 0), depthRight);
    float4 posTop = depthToWorld(uv - float2(0, texelSize.y), depthTop);
    float4 posBottom = depthToWorld(uv + float2(0, texelSize.y), depthBottom);

    float3 dX = posRight.xyz - posLeft.xyz;
    float3 dY = posBottom.xyz - posTop.xyz;

    float3 normal = normalize(cross(dX, dY));

    return normal;
}

[shader("raygeneration")]
void RayGen()
{
    uint2 index = DispatchRaysIndex().xy;
    uint2 dim = DispatchRaysDimensions().xy;

    float depth = DepthTexture.Load(int3(index, 0));

    // If the pixel was clipped/nothing drawn
    if (depth >= 1.0) {
        ShadowMask[index] = 0.0;
        return;
    }

    float2 uv = (index + 0.5) / dim;
    float4 worldPos = depthToWorld(uv, depth);

    float3 origin = worldPos.xyz;
    float3 direction = normalize(LightPosition - worldPos.xyz);
    //float3 normal = depthToNormal(index, uv, depth, 1.0 / dim);
    //float3 randomDir = normalize(float3(nrand(uv, depth), nrand(uv + float2(0.1, 0.2), depth), direction.z));

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.001;
    ray.TMax = distance(LightPosition, worldPos.xyz);

    ShadowPayload payload = { false, 0.0 };
    // Cool optimization here since we don't need any material info
    TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, ~0, 0, 1, 0, ray, payload);

    ShadowMask[index] = float2(payload.hit ? 1.0 : 0.0, payload.t);
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
