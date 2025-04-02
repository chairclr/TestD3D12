#include "TriangleCommon.hlsl"

float4 PSMain(PS_INPUT input) : SV_Target {
    float4 output_color = float4(input.color, 1.0);

    return output_color;
}
