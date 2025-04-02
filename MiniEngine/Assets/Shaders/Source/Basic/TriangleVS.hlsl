#include "TriangleCommon.hlsl"

cbuffer constants : register(b0) {
    column_major float4x4 ViewProjectionMatrix; 
};

PS_INPUT VSMain(VS_INPUT input) {
    PS_INPUT output;
    
    float4 worldPosition = mul(ViewProjectionMatrix, float4(input.pos, 1.0));
    
    output.pos = worldPosition;
    output.color = input.color;
    
    return output;
}
