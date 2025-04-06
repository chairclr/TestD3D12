#include "TriangleCommon.hlsl"

cbuffer constants : register(b0) {
    column_major float4x4 ViewProjection; 
};

PS_INPUT VSMain(VS_INPUT input) {
    PS_INPUT output;
    
    float4 pos = mul(ViewProjection, float4(input.pos, 1.0));
    
    output.pos = pos;
    output.normal = input.normal;
    
    return output;
}
