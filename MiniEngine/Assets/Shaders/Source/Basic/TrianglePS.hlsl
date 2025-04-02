#include "TriangleCommon.hlsl"

PS_OUTPUT PSMain(PS_INPUT input)
{
    PS_OUTPUT output;
    
    output.color = float4(input.color, 1.0);

    return output;
}
