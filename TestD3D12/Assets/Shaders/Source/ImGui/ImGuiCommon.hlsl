struct VS_INPUT
{
   float2 pos : POSITION;
   float2 uv  : TEXCOORD;
   float4 col : COLOR;
};
            
struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float4 col : COLOR;
    float2 uv  : TEXCOORD;
};
