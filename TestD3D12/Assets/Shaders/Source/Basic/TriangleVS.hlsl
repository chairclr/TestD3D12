#include "Common.hlsl"

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    
    float4 worldPosition = float4(input.pos, 1.0);
    
    output.pos = worldPosition;
    output.color = input.color;
    
    return output;
}
