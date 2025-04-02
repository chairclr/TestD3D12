#include "TriangleCommon.hlsl"

cbuffer constants : register(b0)
{
    row_major float4x4 ViewProjectionMatrix; 
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    
    float4 worldPosition = mul(float4(input.pos, 1.0), ViewProjectionMatrix);
    
    output.pos = worldPosition;
    output.color = input.color;
    
    return output;
}
