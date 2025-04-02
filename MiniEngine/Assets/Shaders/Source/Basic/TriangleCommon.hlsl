struct VS_INPUT
{
    float3 pos : POSITION;
    float3 color : COLOR;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float3 color : COLOR;
};
