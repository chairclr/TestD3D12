#include "FullscreenCommon.hlsl"

// We just take in the vertex id and use them to get actual vertex coords, then output to ps
PS_INPUT VSMain(uint vertexID: SV_VertexID) {
    PS_INPUT output;

    float x = 0.0;
    float y = 3.0;
    float z = 0.0;

    if (vertexID == 1) {
        x = 3.0;
        y = -3.0;
    } else if (vertexID == 2) {
        x = -3.0;
        y = -3.0;
    }

    output.position = float4(x, y, z, 1.0);
    output.uv = float2(x, -y) * 0.5 + 0.5; // Convert from NDC to UV space

    return output;
}

