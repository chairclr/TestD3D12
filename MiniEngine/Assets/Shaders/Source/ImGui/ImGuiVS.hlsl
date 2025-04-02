#include "ImGuiCommon.hlsl"

cbuffer constants : register(b0) {
    column_major float4x4 ProjectionMatrix; 
};

PS_INPUT VSMain(VS_INPUT input) {
    PS_INPUT output;

    output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
    output.col = input.col;
    output.uv  = input.uv;

    return output;
}
