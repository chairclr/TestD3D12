#include "ImGuiCommon.hlsl"

SamplerState MainSampler : register(s0);
//Texture2D Texture : register(t0);
            
float4 PSMain(PS_INPUT input) : SV_Target
{
    float4 out_col = input.col; 

    return out_col; 
}
