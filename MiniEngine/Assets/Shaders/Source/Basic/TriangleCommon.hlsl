struct VS_INPUT {
    float3 pos : POSITION;
    float3 normal : NORMAL;
};

struct PS_INPUT {
    float4 pos : SV_Position;
    float3 normal : COLOR;
};
